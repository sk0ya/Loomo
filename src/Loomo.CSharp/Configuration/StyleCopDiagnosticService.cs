using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;
using LspDiagnosticSeverity = Editor.Core.Lsp.DiagnosticSeverity;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>StyleCop.Analyzers をRoslynの正規Analyzerとして実行するIDE用フォールバック。</summary>
/// <remarks>
/// 通常はRoslyn LSPの診断を正本とする。LSPがStyleCop診断を返さない環境だけ、同じAnalyzer DLLを
/// Loomo.CSharpから直接実行する。ルールを再実装しないため、Buildと同じID／本文／位置を保てる。
/// </remarks>
public sealed class StyleCopDiagnosticService
{
    private readonly StyleCopConfigurationService _configuration;
    private readonly CSharpEditorConfigService _editorConfig;

    public StyleCopDiagnosticService(
        StyleCopConfigurationService? configuration = null,
        CSharpEditorConfigService? editorConfig = null)
    {
        _configuration = configuration ?? new StyleCopConfigurationService();
        _editorConfig = editorConfig ?? new CSharpEditorConfigService();
    }

    public async Task<StyleCopAnalysisResult> AnalyzeAsync(
        ProjectModel project,
        string filePath,
        string source,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fullPath = Path.GetFullPath(filePath);
        var configuration = _configuration.Resolve(project);
        // Analyzer未導入／未読込は「違反0件」でも「解析失敗」でもない。設定モデルが
        // 状態を明示しているため、ここでは偽の(0,0)診断を作らず、UIが状態表示だけを
        // 出せるようにする。
        if (!configuration.IsInstalled ||
            configuration.State == StyleCopConfigurationState.AnalyzerNotLoaded)
            return new([], null);

        try
        {
            var analyzerReferences = configuration.AnalyzerPaths
                .Where(File.Exists)
                .Select(path => new AnalyzerFileReference(path, SharedAnalyzerAssemblyLoader.Instance))
                .ToArray();
            if (analyzerReferences.Length == 0)
                return new([], "StyleCop Analyzer DLLが見つかりません。");

            var analyzers = analyzerReferences
                .SelectMany(reference => reference.GetAnalyzers(LanguageNames.CSharp))
                .ToArray();
            if (analyzers.Length == 0)
                return new([], "StyleCop AnalyzerからC# Analyzerを取得できません。");

            var target = project.SelectedTargetFrameworkModel;
            var compileFiles = target?.CompileFiles
                .Select(item => Path.GetFullPath(item.FullPath))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            if (!compileFiles.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                compileFiles.Add(fullPath);

            var parseOptions = CSharpProjectCompilationOptions.Parse(target);
            var trees = new List<SyntaxTree>(compileFiles.Count);
            var normalizedOpenTexts = NormalizeOpenTexts(openTexts);
            foreach (var path in compileFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase)
                    ? source
                    : normalizedOpenTexts?.TryGetValue(path, out var openText) == true
                        ? openText
                        : await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions,
                    path, cancellationToken: cancellationToken));
            }

            var references = ResolveReferences(target);
            var compilation = CSharpCompilation.Create(
                Path.GetFileNameWithoutExtension(project.FullPath),
                trees,
                references,
                CSharpProjectCompilationOptions.Compilation(target));
            var additionalFiles = configuration.ConfigurationFiles
                .Where(File.Exists)
                .Select(path => (AdditionalText)new FileAdditionalText(path))
                .ToArray();
            var editorConfig = _editorConfig.Resolve(fullPath);
            var options = new AnalyzerOptions(additionalFiles.ToImmutableArray(),
                new CSharpAnalyzerConfigOptionsProvider(_editorConfig, fullPath));
            var withAnalyzers = compilation.WithAnalyzers(
                analyzers.ToImmutableArray(), options);
            // ライブラリ側でも文脈へ戻さない（UI スレッドから呼ばれても解析を戻り込ませない）。
            var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = diagnostics
                .Where(d => d.Id.StartsWith("SA", StringComparison.OrdinalIgnoreCase))
                .Where(d => d.Location.IsInSource &&
                    string.Equals(d.Location.SourceTree?.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
                .Select(d => ToLspDiagnostic(d, editorConfig, configuration))
                .Where(d => d is not null)
                .Select(d => d!)
                .OrderBy(d => d.Range.Start.Line)
                .ThenBy(d => d.Range.Start.Character)
                .ThenBy(d => d.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   BadImageFormatException or FileLoadException)
        {
            return new([], $"StyleCop解析に失敗しました: {ex.Message}");
        }
    }

    private static IReadOnlyList<MetadataReference> ResolveReferences(TargetFrameworkModel? target)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in target?.References ?? [])
            if (File.Exists(reference.FullPath)) paths.Add(Path.GetFullPath(reference.FullPath));

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
            foreach (var path in trusted.Split(Path.PathSeparator))
                if (File.Exists(path)) paths.Add(path);

        // 参照は共有キャッシュから（毎回作り直さない。MetadataReferenceCache のコメント参照）。
        return paths.Select(static path => MetadataReferenceCache.Get(path))
            .OfType<MetadataReference>().ToArray();
    }

    private static IReadOnlyDictionary<string, string>? NormalizeOpenTexts(
        IReadOnlyDictionary<string, string>? openTexts)
    {
        if (openTexts is null || openTexts.Count == 0) return null;
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in openTexts)
        {
            if (string.IsNullOrWhiteSpace(path) || text is null) continue;
            try { normalized[Path.GetFullPath(path)] = text; }
            catch (ArgumentException) { }
        }
        return normalized.Count == 0 ? null : normalized;
    }

    private static LspDiagnostic? ToLspDiagnostic(
        Diagnostic diagnostic,
        CSharpEditorConfig editorConfig,
        StyleCopConfiguration configuration)
    {
        var severity = ResolveSeverity(diagnostic.Id, editorConfig, configuration);
        if (severity == CSharpDiagnosticSeverity.None)
            return null;
        var span = diagnostic.Location.GetLineSpan();
        var start = span.StartLinePosition;
        var end = span.EndLinePosition;
        return new LspDiagnostic(
            new LspRange(new LspPosition(start.Line, start.Character),
                new LspPosition(end.Line, end.Character)),
            diagnostic.GetMessage(),
            severity switch
            {
                CSharpDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
                CSharpDiagnosticSeverity.Warning or CSharpDiagnosticSeverity.Default => LspDiagnosticSeverity.Warning,
                CSharpDiagnosticSeverity.Suggestion or CSharpDiagnosticSeverity.Silent => LspDiagnosticSeverity.Hint,
                _ => LspDiagnosticSeverity.Warning,
            },
            "StyleCop",
            diagnostic.Id);
    }

    private static CSharpDiagnosticSeverity ResolveSeverity(
        string id,
        CSharpEditorConfig editorConfig,
        StyleCopConfiguration configuration)
    {
        var configured = editorConfig.GetDiagnosticSeverity(id, "StyleCop");
        if (configured != CSharpDiagnosticSeverity.Default)
            return configured;

        var setting = configuration.RuleSettings.LastOrDefault(rule =>
            string.Equals(rule.RuleId, id, StringComparison.OrdinalIgnoreCase));
        setting ??= configuration.RuleSettings.LastOrDefault(rule =>
            rule.RuleId.Equals("all-analyzers", StringComparison.OrdinalIgnoreCase));
        if (setting is null) return CSharpDiagnosticSeverity.Default;
        return setting.Severity.Trim().ToLowerInvariant() switch
        {
            "none" or "off" => CSharpDiagnosticSeverity.None,
            "error" => CSharpDiagnosticSeverity.Error,
            "warning" => CSharpDiagnosticSeverity.Warning,
            "suggestion" or "info" or "information" => CSharpDiagnosticSeverity.Suggestion,
            "silent" or "hidden" => CSharpDiagnosticSeverity.Silent,
            _ => CSharpDiagnosticSeverity.Default,
        };
    }

    private sealed class FileAdditionalText(string path) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => SourceText.From(File.ReadAllText(Path), Encoding.UTF8);
    }

    private sealed class SharedAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static SharedAnalyzerAssemblyLoader Instance { get; } = new();
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath)
            => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
    }
}

public sealed record StyleCopAnalysisResult(
    IReadOnlyList<LspDiagnostic> Diagnostics,
    string? Error);
