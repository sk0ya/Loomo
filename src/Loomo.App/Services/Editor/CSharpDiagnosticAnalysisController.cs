using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Services;

internal sealed record CSharpDiagnosticAnalysisPlan(
    int Version,
    bool LanguageServerOwnsCompilerDiagnostics);

/// <summary>C# の非同期診断を実行し、現行本文の結果だけを診断セッションへ反映する。</summary>
internal sealed class CSharpDiagnosticAnalysisController
{
    private readonly Dispatcher _dispatcher;
    private readonly StyleCopDiagnosticService _styleCop;
    private readonly CSharpCompilerDiagnosticService _compiler;
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _styleCopRuns = [];
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _compilerRuns = [];
    private readonly Func<VimEditorControl, EditorDiagnosticSession> _getSession;
    private readonly Action<VimEditorControl> _refresh;
    private readonly Func<string, ProjectModel?> _projectForFile;
    private readonly Func<SolutionModel?> _currentSolution;
    private readonly Func<IReadOnlyDictionary<string, string>> _openTexts;

    public CSharpDiagnosticAnalysisController(
        Dispatcher dispatcher,
        StyleCopDiagnosticService styleCop,
        CSharpCompilerDiagnosticService compiler,
        Func<VimEditorControl, EditorDiagnosticSession> getSession,
        Action<VimEditorControl> refresh,
        Func<string, ProjectModel?> projectForFile,
        Func<SolutionModel?> currentSolution,
        Func<IReadOnlyDictionary<string, string>> openTexts)
    {
        _dispatcher = dispatcher;
        _styleCop = styleCop;
        _compiler = compiler;
        _getSession = getSession;
        _refresh = refresh;
        _projectForFile = projectForFile;
        _currentSolution = currentSolution;
        _openTexts = openTexts;
    }

    public void Schedule(VimEditorControl control, Action<VimEditorControl> clear)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => Schedule(control, clear)), DispatcherPriority.DataBind);
            return;
        }

        if (control.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            clear(control);
            return;
        }

        var source = control.Text;
        var expectedPath = Path.GetFullPath(path);
        var session = _getSession(control);
        var project = _projectForFile(path);
        var solution = _currentSolution();
        var plan = BeginSession(session, expectedPath, source, project is not null, solution is not null,
            control.LspDocument, control.LspDiagnostics);
        if (_styleCopRuns.TryGetValue(control, out var previousStyleCop))
            previousStyleCop.Cancel();
        if (_compilerRuns.TryGetValue(control, out var previousCompiler))
            previousCompiler.Cancel();

        if (project is null)
        {
            _refresh(control);
            return;
        }

        var openTexts = _openTexts();
        var styleCopCts = new CancellationTokenSource();
        _styleCopRuns[control] = styleCopCts;
        _refresh(control);
        _ = AnalyzeStyleCopAsync(control, project, expectedPath, source, openTexts, plan.Version, styleCopCts);

        var compilerCts = new CancellationTokenSource();
        if (plan.LanguageServerOwnsCompilerDiagnostics)
        {
            _compilerRuns.Remove(control);
            compilerCts.Dispose();
        }
        else if (_currentSolution() is { } compilerSolution)
        {
            _compilerRuns[control] = compilerCts;
            _ = AnalyzeCompilerAsync(control, compilerSolution, expectedPath, source, openTexts, plan.Version, compilerCts);
        }
        else
        {
            compilerCts.Dispose();
        }
    }

    public EditorDiagnosticSession EnsureScheduled(
        VimEditorControl control, string filePath, string text, Action<VimEditorControl> schedule)
    {
        var session = _getSession(control);
        if (NeedsScheduling(session, filePath, text))
        {
            // 未開始の版を「診断0件」として確定させないよう、Quick Fixより先に解析を開始する。
            if (_dispatcher.CheckAccess())
                schedule(control);
            else
                _dispatcher.Invoke(() => schedule(control), DispatcherPriority.DataBind);
            session = _getSession(control);
        }
        return session;
    }

    public void Cancel(VimEditorControl control)
    {
        if (_styleCopRuns.Remove(control, out var styleCopCts))
            styleCopCts.Cancel();
        if (_compilerRuns.Remove(control, out var compilerCts))
            compilerCts.Cancel();
    }

    public void CancelAll()
    {
        foreach (var cts in _styleCopRuns.Values.ToArray()) cts.Cancel();
        foreach (var cts in _styleCopRuns.Values.ToArray()) cts.Dispose();
        foreach (var cts in _compilerRuns.Values.ToArray()) cts.Cancel();
        foreach (var cts in _compilerRuns.Values.ToArray()) cts.Dispose();
        _styleCopRuns.Clear();
        _compilerRuns.Clear();
    }

    /// <summary>解析元とLSP診断の版を診断セッションへ反映し、この版で起動する解析を決める。</summary>
    public CSharpDiagnosticAnalysisPlan BeginSession(
        EditorDiagnosticSession session,
        string expectedPath,
        string source,
        bool hasProject,
        bool hasSolution,
        ILspDocument? document,
        IReadOnlyList<LspDiagnostic> lspDiagnostics)
    {
        var previousPresentation = session.Presentation;
        var languageServerReady = document is { IsReady: true };
        var expectedOrigins = new List<EditorDiagnosticOrigin>();
        if (languageServerReady) expectedOrigins.Add(EditorDiagnosticOrigin.LanguageServer);
        if (hasProject) expectedOrigins.Add(EditorDiagnosticOrigin.StyleCop);
        if (!languageServerReady && hasProject && hasSolution)
            expectedOrigins.Add(EditorDiagnosticOrigin.Compiler);

        var version = session.Begin(expectedPath, source, expectedOrigins);
        if (languageServerReady && document is { Version: { } lspVersion } currentDocument &&
            string.Equals(currentDocument.Text, source, StringComparison.Ordinal) &&
            previousPresentation is { } previousSnapshot &&
            string.Equals(previousSnapshot.Text, source, StringComparison.Ordinal) &&
            previousSnapshot.LanguageServerVersion == lspVersion)
        {
            // 本文もLSP版も同じ場合だけ前回のpush診断を新しい解析回へ引き継ぐ。
            session.Publish(version, EditorDiagnosticOrigin.LanguageServer, lspDiagnostics, lspVersion);
        }

        var languageServerOwnsCompilerDiagnostics = languageServerReady && document is { } readyDocument &&
            string.Equals(Path.GetFullPath(readyDocument.FilePath), expectedPath,
                StringComparison.OrdinalIgnoreCase);
        return new(version, languageServerOwnsCompilerDiagnostics);
    }

    public static bool NeedsScheduling(EditorDiagnosticSession session, string filePath, string text)
    {
        var snapshot = session.Presentation;
        return session.Version == 0 || snapshot is null || snapshot.Version != session.Version ||
            !string.Equals(session.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.Text, text, StringComparison.Ordinal);
    }

    /// <summary>本文が一致するLSP診断を診断セッションへ反映し、重複するcompiler解析を止める。</summary>
    public bool TryPublishLanguageServerDiagnostics(
        VimEditorControl control, EditorDiagnosticSession session)
    {
        var document = control.LspDocument;
        if (document is not { IsReady: true, Version: { } lspVersion } ||
            !string.Equals(document.Text, control.Text, StringComparison.Ordinal))
            return false;

        var version = session.Version;
        session.Skip(version, EditorDiagnosticOrigin.Compiler);
        session.Publish(version, EditorDiagnosticOrigin.LanguageServer, control.LspDiagnostics, lspVersion);

        // LSPがcompiler診断を返し始めた後は、同じCompilationを作り直すfallbackを止める。
        if (_compilerRuns.Remove(control, out var cts))
            cts.Cancel();
        _refresh(control);
        return true;
    }

    public async Task AnalyzeStyleCopAsync(
        VimEditorControl control,
        ProjectModel project,
        string expectedPath,
        string source,
        IReadOnlyDictionary<string, string> openTexts,
        int version,
        CancellationTokenSource cts)
    {
        try
        {
            // 入力中に毎キーで Roslyn Compilation を作らない。最後の入力だけを解析する。
            // fire-and-forget の解析を UI スレッドへ戻さないよう、待機の続きはワーカースレッドで実行する。
            await Task.Delay(300, cts.Token).ConfigureAwait(false);
            var result = await _styleCop
                .AnalyzeAsync(project, expectedPath, source, cts.Token, openTexts).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(control, expectedPath, source, cts, _styleCopRuns)) return;
                var diagnostics = result.Error is null
                    ? result.Diagnostics
                    : [CSharpEditorResultMapper.AnalysisFailure(result.Error, "StyleCop")];
                _getSession(control).Publish(version, EditorDiagnosticOrigin.StyleCop, diagnostics);
                _refresh(control);
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && IsRegistered(control, cts, _styleCopRuns))
                {
                    _getSession(control).Publish(version, EditorDiagnosticOrigin.StyleCop,
                        [CSharpEditorResultMapper.AnalysisFailure($"StyleCop解析に失敗しました: {ex.Message}", "StyleCop")]);
                    _refresh(control);
                }
            });
        }
        finally
        {
            await RemoveRunAsync(control, cts, _styleCopRuns);
        }
    }

    public async Task AnalyzeCompilerAsync(
        VimEditorControl control,
        SolutionModel solution,
        string expectedPath,
        string source,
        IReadOnlyDictionary<string, string> openTexts,
        int version,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(300, cts.Token).ConfigureAwait(false);
            var result = await _compiler
                .AnalyzeAsync(solution, expectedPath, source, cts.Token, openTexts).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(control, expectedPath, source, cts, _compilerRuns)) return;
                var diagnostics = result.Error is null
                    ? result.Diagnostics
                    : [CSharpEditorResultMapper.AnalysisFailure(result.Error, "Compiler")];
                _getSession(control).Publish(version, EditorDiagnosticOrigin.Compiler, diagnostics);
                _refresh(control);
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && IsRegistered(control, cts, _compilerRuns))
                {
                    _getSession(control).Publish(version, EditorDiagnosticOrigin.Compiler,
                        [CSharpEditorResultMapper.AnalysisFailure($"C# compiler解析に失敗しました: {ex.Message}", "Compiler")]);
                    _refresh(control);
                }
            });
        }
        finally
        {
            await RemoveRunAsync(control, cts, _compilerRuns);
        }
    }

    private bool IsCurrent(
        VimEditorControl control,
        string expectedPath,
        string source,
        CancellationTokenSource cts,
        IDictionary<VimEditorControl, CancellationTokenSource> runs)
        => !cts.IsCancellationRequested
            && IsRegistered(control, cts, runs)
            && string.Equals(Path.GetFullPath(control.FilePath ?? ""), expectedPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(control.Text, source, StringComparison.Ordinal);

    private static bool IsRegistered(
        VimEditorControl control,
        CancellationTokenSource cts,
        IDictionary<VimEditorControl, CancellationTokenSource> runs)
        => runs.TryGetValue(control, out var current) && ReferenceEquals(current, cts);

    private async Task RemoveRunAsync(
        VimEditorControl control,
        CancellationTokenSource cts,
        IDictionary<VimEditorControl, CancellationTokenSource> runs)
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (IsRegistered(control, cts, runs))
                    runs.Remove(control);
            });
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted) { }
        cts.Dispose();
    }
}
