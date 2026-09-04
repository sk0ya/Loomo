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

        var references = ResolveReferences(referencePaths);
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

    private static IReadOnlyList<MetadataReference> ResolveReferences(IEnumerable<string>? referencePaths)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (referencePaths is not null)
        {
            foreach (var path in referencePaths)
                AddPath(path, paths);
        }

        // Unit tests and csproj評価前の編集ではReferencePathが空になることがある。
        // 実行中の.NETが提供する標準参照を足して、string／LINQ等の意味解決を可能にする。
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trusted))
        {
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                AddPath(path, paths);
        }

        // 参照は必ず共有キャッシュ経由で取る（毎回作り直すとヒープが膨らんで
        // ブロッキング GC で UI が止まる。<see cref="MetadataReferenceCache"/> 参照）。
        var references = new List<MetadataReference>(paths.Count);
        foreach (var path in paths)
            if (MetadataReferenceCache.Get(path) is { } reference)
                references.Add(reference);
        return references;
    }

    private static void AddPath(string? path, ISet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) &&
                string.Equals(Path.GetExtension(full), ".dll", StringComparison.OrdinalIgnoreCase))
                paths.Add(full);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
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
