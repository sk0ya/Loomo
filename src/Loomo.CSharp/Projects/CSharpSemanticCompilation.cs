using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using System.Runtime.Loader;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>
/// C#の意味モデルを、既に読み込んだワークスペースソースとMSBuild評価済み参照から一度だけ作る。
/// MSBuildWorkspaceや独自の解析プロセスを操作ごとに起動せず、C#固有DLL内のリファクタリングへ
/// 同じCompilationを渡すための共有境界である。
/// </summary>
public static class CSharpSemanticCompilation
{
    /// <summary>テストや構文fallbackからも利用できる、ソース辞書ベースのCompilationを作る。</summary>
    public static CSharpCompilation Create(
        IReadOnlyDictionary<string, string> sourceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? parseOptionsByPath = null,
        IEnumerable<string>? referencePaths = null,
        string? assemblyName = null,
        CSharpCompilationOptions? compilationOptions = null,
        IEnumerable<string>? analyzerPaths = null,
        IEnumerable<AdditionalText>? additionalTexts = null,
        AnalyzerConfigOptionsProvider? analyzerConfigOptionsProvider = null)
    {
        var trees = sourceTexts
            .Where(pair => string.Equals(Path.GetExtension(pair.Key), ".cs", StringComparison.OrdinalIgnoreCase))
            .Select(pair =>
            {
                var path = Path.GetFullPath(pair.Key);
                var parseOptions = parseOptionsByPath is not null &&
                    parseOptionsByPath.TryGetValue(pair.Key, out var configured)
                    ? configured
                    : parseOptionsByPath is not null &&
                      parseOptionsByPath.TryGetValue(path, out configured)
                        ? configured
                        : CSharpParseOptions.Default;
                return CSharpSyntaxTree.ParseText(SourceText.From(pair.Value), parseOptions, path);
            })
            .ToImmutableArray<SyntaxTree>();

        var references = ResolveReferences(referencePaths, assemblyName);
        var compilation = CSharpCompilation.Create(
            assemblyName ?? "Loomo.CSharp.Workspace",
            trees,
            references,
            compilationOptions ?? new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                warningLevel: 4));
        return analyzerPaths is null
            ? compilation
            : RunSourceGenerators(compilation, analyzerPaths, additionalTexts,
                analyzerConfigOptionsProvider);
    }

    /// <summary>Compilationから、パスに一致する文書のSemanticModelを取得する。</summary>
    public static SemanticModel? ForFile(CSharpCompilation compilation, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
        return tree is null ? null : compilation.GetSemanticModel(tree, ignoreAccessibility: false);
    }

    /// <summary>
    /// 編集中プロジェクトのメタデータ参照を決める。正本は <b>MSBuild が解決した参照</b>
    /// （<c>@(ReferencePath)</c>）だけで、実行中の Loomo 自身が読み込んでいるアセンブリは混ぜない。
    ///
    /// <para><b>なぜ厳しくするか</b>——以前は <c>TRUSTED_PLATFORM_ASSEMBLIES</c>（＝実行中の
    /// Loomo.App が読み込む全 DLL）を無条件に足していた。これは「編集中プロジェクトの参照」ではなく
    /// 「Loomo というアプリの中身」なので、2つ壊れる。(1) Loomo で Loomo 自身を編集すると、
    /// ソース側の型と走っている <c>sk0ya.Loomo.Services.dll</c> の型が衝突して CS0436 が出る。
    /// (2) 他人のプロジェクトを編集しているときも、そのプロジェクトが参照していない Loomo の依存
    /// （Editor / Roslyn / CommunityToolkit…）が見えてしまい、診断がそのプロジェクトの意味から離れる。</para>
    ///
    /// <para>MSBuild 参照が1件も無いとき（未評価・未restore・単体テスト）だけ、標準ライブラリの
    /// 代わりに TPA を使う。その場合も<b>実行中アプリの出力ディレクトリにある DLL は除く</b>ので、
    /// 足されるのは共有フレームワーク（System.*／WPF）だけになる。</para>
    /// </summary>
    public static IReadOnlyList<MetadataReference> ResolveReferences(
        IEnumerable<string>? referencePaths, string? assemblyName = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentProcessAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (referencePaths is not null)
        {
            foreach (var path in referencePaths)
                AddPath(path, paths, assemblyName, currentProcessAssemblyName);
        }

        if (paths.Count == 0)
        {
            foreach (var path in SharedFrameworkFallback())
                AddPath(path, paths, assemblyName, currentProcessAssemblyName);
        }

        // 参照は必ず共有キャッシュ経由で取る（毎回作り直すとヒープが膨らんで
        // ブロッキング GC で UI が止まる。<see cref="MetadataReferenceCache"/> 参照）。
        var references = new List<MetadataReference>(paths.Count);
        foreach (var path in paths)
            if (MetadataReferenceCache.Get(path) is { } reference)
                references.Add(reference);
        return references;
    }

    /// <summary>MSBuild 参照が無いときだけ使う標準参照。TPA から<b>実行中アプリ自身の出力
    /// ディレクトリの DLL を除いた</b>もの、つまり共有フレームワークだけを返す。</summary>
    private static IEnumerable<string> SharedFrameworkFallback()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted ||
            string.IsNullOrWhiteSpace(trusted)) yield break;
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (!IsHostAssembly(path)) yield return path;
    }

    private static readonly string HostDirectory = NormalizeDirectory(AppContext.BaseDirectory);

    /// <summary>実行中のアプリ（Loomo 本体・テストホスト）が自分の出力として抱えている DLL か。</summary>
    private static bool IsHostAssembly(string path)
    {
        try
        {
            return HostDirectory.Length > 0 && string.Equals(
                NormalizeDirectory(Path.GetDirectoryName(Path.GetFullPath(path))),
                HostDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }

    private static string NormalizeDirectory(string? directory)
        => string.IsNullOrWhiteSpace(directory)
            ? ""
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static void AddPath(
        string? path, ISet<string> paths, string? assemblyName, string? currentProcessAssemblyName)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) &&
                string.Equals(Path.GetExtension(full), ".dll", StringComparison.OrdinalIgnoreCase) &&
                !IsSelfAssembly(full, assemblyName, currentProcessAssemblyName))
                paths.Add(full);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// 編集中プロジェクト自身の出力 DLL を参照へ混ぜない（ソース側の型と衝突して CS0436 になる）。
    ///
    /// <para><paramref name="assemblyName"/> には<b>MSBuild が評価した実アセンブリ名</b>を渡すこと。
    /// Loomo は <c>Loomo.Services.csproj</c> → <c>sk0ya.Loomo.Services</c> のようにプロジェクト
    /// ファイル名とアセンブリ名が違うので、csproj のファイル名を渡すとこの判定がすり抜ける
    /// （<see cref="ProjectModel.CompilationAssemblyName"/>）。</para>
    /// </summary>
    private static bool IsSelfAssembly(string path, string? assemblyName, string? currentProcessAssemblyName)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return (!string.IsNullOrWhiteSpace(assemblyName) &&
                   string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(currentProcessAssemblyName) &&
                   string.Equals(name, currentProcessAssemblyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>MSBuildのAnalyzer項目に含まれるSource GeneratorだけをRoslyn公式APIで実行する。
    /// 通常のAnalyzerはGenerator型を持たないため無変更で、ロード失敗したAnalyzerもcompiler fallback
    /// 全体を壊さず読み飛ばす。生成されたSyntaxTreeは返却Compilationへ統合される。</summary>
    private static CSharpCompilation RunSourceGenerators(
        CSharpCompilation compilation,
        IEnumerable<string> analyzerPaths,
        IEnumerable<AdditionalText>? additionalTexts,
        AnalyzerConfigOptionsProvider? analyzerConfigOptionsProvider)
    {
        var generators = new List<ISourceGenerator>();
        var loadContexts = new List<AnalyzerAssemblyLoadContext>();
        try
        {
            foreach (var rawPath in analyzerPaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                try
                {
                    var path = Path.GetFullPath(rawPath);
                    if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".dll",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    var loadContext = new AnalyzerAssemblyLoadContext(path);
                    loadContexts.Add(loadContext);
                    // 元DLLをLoadFromAssemblyPathするとWindowsでファイルがロックされ、
                    // IDEの直後のBuildを妨げる。ストリーム経由なら元ファイルを開放したまま
                    // Roslyn共通アセンブリだけ既定コンテキストと共有できる。
                    using var stream = File.OpenRead(path);
                    var assembly = loadContext.LoadFromStream(stream);
                    foreach (var type in GetLoadableTypes(assembly))
                    {
                        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) continue;
                        if (typeof(IIncrementalGenerator).IsAssignableFrom(type) &&
                            Activator.CreateInstance(type) is IIncrementalGenerator incremental)
                        {
                            generators.Add(incremental.AsSourceGenerator());
                        }
                        else if (typeof(ISourceGenerator).IsAssignableFrom(type) &&
                                 Activator.CreateInstance(type) is ISourceGenerator source)
                        {
                            generators.Add(source);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    BadImageFormatException or FileLoadException or FileNotFoundException or
                    ReflectionTypeLoadException or InvalidOperationException or
                    MemberAccessException or TypeLoadException)
                {
                    // Analyzerの依存関係不足は、生成ソースが得られないだけのrecoverable状態。
                }
            }

            if (generators.Count == 0) return compilation;
            var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
                ?? CSharpParseOptions.Default;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators,
                additionalTexts: additionalTexts?.ToImmutableArray() ?? [],
                parseOptions: parseOptions,
                optionsProvider: analyzerConfigOptionsProvider);
            driver = driver.RunGeneratorsAndUpdateCompilation(
                compilation, out var updated, out _);
            return (CSharpCompilation)updated;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return compilation;
        }
        finally
        {
            foreach (var loadContext in loadContexts) loadContext.Unload();
        }
    }

    /// <summary>MSBuildのAdditionalFilesをGeneratorDriverへ渡すための読み取り専用実装を作る。</summary>
    public static IReadOnlyList<AdditionalText> CreateAdditionalTexts(IEnumerable<string>? paths)
        => (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (AdditionalText)new FileAdditionalText(path))
            .ToArray();

    private sealed class FileAdditionalText(string path) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return SourceText.From(File.ReadAllText(Path)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Analyzer本体と依存DLLをストリームから読む。Roslyn本体は既定コンテキストを共有する。</summary>
    private sealed class AnalyzerAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public AnalyzerAssemblyLoadContext(string analyzerPath)
            : base(isCollectible: true)
            => _resolver = new AssemblyDependencyResolver(analyzerPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (shared is not null) return shared;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return LoadFromStream(stream);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
    }
}
