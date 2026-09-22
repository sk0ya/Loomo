using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using sk0ya.Loomo.CSharp.Projects;
using LspDiagnosticSeverity = Editor.Core.Lsp.DiagnosticSeverity;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>Roslyn Language Serverが未接続・診断未対応の間も、編集中のC#文書へ
/// compiler診断を返すフォールバック。プロジェクトの選択TFM・未保存本文・参照を共有し、
/// StyleCopやLSPとは別の発生源として扱う。</summary>
public sealed class CSharpCompilerDiagnosticService
{
    private readonly CSharpEditorConfigService _editorConfig;

    public CSharpCompilerDiagnosticService(CSharpEditorConfigService? editorConfig = null)
        => _editorConfig = editorConfig ?? new CSharpEditorConfigService();

    public async Task<CSharpCompilerAnalysisResult> AnalyzeAsync(
        SolutionModel solution,
        string filePath,
        string source,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var fullPath = Path.GetFullPath(filePath);
        var project = solution.ProjectForFile(fullPath);
        if (project is null)
            return new([], "C#プロジェクトを解決できません。");
        if (project.State != ProjectLoadState.Ready)
            return new([], "C#プロジェクトを読み込み中です。");

        try
        {
            var target = project.SelectedTargetFrameworkModel;
            var editorConfig = _editorConfig.Resolve(fullPath);
            // Compilation の生成も診断の取得も Task.Run の<b>中</b>で完結させる。
            // GetDiagnostics() が意味解析の本体で、ここが一番重い——await の後ろに置くと、
            // UI スレッドから呼ばれたときに続きがディスパッチャへ戻って数秒固まる（実測3.9秒）。
            var (diagnostics, warning) = await Task.Run(() =>
            {
                var compilation = CSharpWorkspaceOperationContext.Create(
                    solution, fullPath, source,
                    includeSemanticCompilation: true,
                    compilationOptions: CSharpProjectCompilationOptions.Compilation(target, editorConfig),
                    assemblyName: project.CompilationAssemblyName,
                    openTexts: openTexts,
                    // 信用できない状態なら Compilation を組ませない（どのみち捨てるので）
                    requireTrustedSources: true);
                // ソースを積みきれなかったときは<b>何も出さないが、黙らない</b>。欠けたのはこちらの
                // 都合なのに、出る診断は「型が見つからない」（CS0246／CS0103）で、コードの誤りと
                // 区別が付かない——本物の誤りがその中に埋もれる。かといって空を返すだけでは
                // 「問題なし」と見分けが付かないので、理由を添えて返す（Quick Fix 側は前から同じ
                // 条件で降りている＝CSharpCompilerCodeFixService）。これはフォールバックなので、
                // 言語サーバーが入っていればそちらの診断が出る。
                if (compilation.SemanticTrustWarning is { } incomplete)
                    return (Array.Empty<LspDiagnostic>(), incomplete);
                var allDiagnostics = compilation.SemanticCompilation!.GetDiagnostics(cancellationToken)
                    .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Location.IsInSource)
                    .Where(diagnostic => string.Equals(
                        Path.GetFullPath(diagnostic.Location.SourceTree?.FilePath ?? ""),
                        fullPath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var diagnostics = allDiagnostics
                    .Where(diagnostic => diagnostic.Severity is RoslynDiagnosticSeverity.Error
                        or RoslynDiagnosticSeverity.Warning or RoslynDiagnosticSeverity.Info)
                    .Select(ToLspDiagnostic)
                    .OrderBy(diagnostic => diagnostic.Range.Start.Line)
                    .ThenBy(diagnostic => diagnostic.Range.Start.Character)
                    .ThenBy(diagnostic => diagnostic.Code, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (diagnostics, (string?)null);
            }, cancellationToken).ConfigureAwait(false);
            return new(diagnostics, warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException)
        {
            return new([], $"C# compiler解析に失敗しました: {ex.Message}");
        }
    }

    private static LspDiagnostic ToLspDiagnostic(Diagnostic diagnostic)
    {
        return new(
            ToLspRange(diagnostic),
            diagnostic.GetMessage(),
            diagnostic.Severity switch
            {
                RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
                RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
                RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Information,
                _ => LspDiagnosticSeverity.Hint,
            },
            "Compiler", diagnostic.Id,
            string.IsNullOrWhiteSpace(diagnostic.Descriptor.HelpLinkUri) ? null : diagnostic.Descriptor.HelpLinkUri,
            // 「消しても構わない」印（未使用の using・到達しないコード）は、Roslyn 自身が
            // CustomTags で言っている。LSP 経由の診断と同じ見せ方（波線ではなく薄字）に揃える。
            diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary)
                ? [DiagnosticTag.Unnecessary]
                : null);
    }

    private static LspRange ToLspRange(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new LspRange(
            new LspPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
            new LspPosition(span.EndLinePosition.Line, span.EndLinePosition.Character));
    }
}

public sealed record CSharpCompilerAnalysisResult(
    IReadOnlyList<LspDiagnostic> Diagnostics,
    string? Error);
