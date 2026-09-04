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
            var diagnostics = await Task.Run(() =>
            {
                var compilation = CSharpWorkspaceOperationContext.Create(
                    solution, fullPath, source,
                    includeSemanticCompilation: true,
                    compilationOptions: CSharpProjectCompilationOptions.Compilation(target, editorConfig),
                    assemblyName: project.Name,
                    openTexts: openTexts);
                return compilation.SemanticCompilation!.GetDiagnostics(cancellationToken)
                .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Location.IsInSource)
                .Where(diagnostic => string.Equals(
                    Path.GetFullPath(diagnostic.Location.SourceTree?.FilePath ?? ""),
                    fullPath, StringComparison.OrdinalIgnoreCase))
                .Where(diagnostic => diagnostic.Severity is RoslynDiagnosticSeverity.Error
                    or RoslynDiagnosticSeverity.Warning or RoslynDiagnosticSeverity.Info)
                .Select(ToLspDiagnostic)
                .OrderBy(diagnostic => diagnostic.Range.Start.Line)
                .ThenBy(diagnostic => diagnostic.Range.Start.Character)
                .ThenBy(diagnostic => diagnostic.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            }, cancellationToken).ConfigureAwait(false);
            return new(diagnostics, null);
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
        var span = diagnostic.Location.GetLineSpan();
        var start = span.StartLinePosition;
        var end = span.EndLinePosition;
        return new(
            new LspRange(
                new LspPosition(start.Line, start.Character),
                new LspPosition(end.Line, end.Character)),
            diagnostic.GetMessage(),
            diagnostic.Severity switch
            {
                RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
                RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
                RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Information,
                _ => LspDiagnosticSeverity.Hint,
            },
            "Compiler", diagnostic.Id);
    }
}

public sealed record CSharpCompilerAnalysisResult(
    IReadOnlyList<LspDiagnostic> Diagnostics,
    string? Error);
