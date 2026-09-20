using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using Editor.Controls;
using Editor.Controls.HostIntegration;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Views;

/// <summary>Loomo.CSharpのStyleCopフォールバックを各C#バッファへ同期する。</summary>
public partial class ShellWindow
{
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _styleCopAnalysisCts = [];
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _compilerAnalysisCts = [];
    private readonly Dictionary<VimEditorControl, EditorDiagnosticSession> _diagnosticSessions = [];
    private readonly Dictionary<VimEditorControl, IReadOnlyList<LspRange>> _compilerUnusedUsingRanges = [];
    private readonly Dictionary<VimEditorControl, int> _compilerUnusedUsingVersions = [];

    private static IReadOnlyList<LspDiagnostic> EditorLspDiagnostics(VimEditorControl control)
    {
        return control.LspDiagnostics;
    }

    private void InitializeCSharpDiagnosticsWiring()
    {
        if (_solutionModel is not null)
            _solutionModel.Changed += OnCSharpSolutionChanged;
    }

    private void OnCSharpSolutionChanged(object? sender, SolutionModel model)
    {
        foreach (var tab in _editorTabs.Where(tab => tab.IsRealized))
            ScheduleStyleCopAnalysis(tab.Control);
    }

    private void OnStyleCopLspDiagnosticsChanged(object? sender, EventArgs e)
    {
        if (sender is not VimEditorControl control || control.FilePath is not { Length: > 0 } path)
            return;

        if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            RefreshStyleCopPresentation(control);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var session = GetDiagnosticSession(control);
        var version = session.Ensure(fullPath, control.Text);
        var document = control.LspDocument;
        if (document is not { IsReady: true, Version: { } lspVersion } ||
            !string.Equals(document.Text, control.Text, StringComparison.Ordinal))
            return;

        session.Skip(version, EditorDiagnosticOrigin.Compiler);
        session.Publish(version, EditorDiagnosticOrigin.LanguageServer, EditorLspDiagnostics(control), lspVersion);

        // LSP が compiler 診断を返し始めた後は、同じ Compilation を作り直す fallback を止める。
        if (_compilerAnalysisCts.Remove(control, out var cts))
        {
            cts.Cancel();
        }

        RefreshStyleCopPresentation(control);
    }

    private void ScheduleStyleCopAnalysis(VimEditorControl control)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ScheduleStyleCopAnalysis(control)), DispatcherPriority.DataBind);
            return;
        }

        if (control.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            ClearStyleCopPresentation(control);
            return;
        }

        var source = control.Text;
        var expectedPath = Path.GetFullPath(path);
        var session = GetDiagnosticSession(control);
        var previousPresentation = session.Presentation;
        var project = _solutionModel?.ProjectForFile(path);
        var languageServerReady = control.LspDocument is { IsReady: true };
        var expectedOrigins = new List<EditorDiagnosticOrigin>();
        if (languageServerReady) expectedOrigins.Add(EditorDiagnosticOrigin.LanguageServer);
        if (project is not null) expectedOrigins.Add(EditorDiagnosticOrigin.StyleCop);
        if (!languageServerReady && project is not null && _solutionModel?.Current is not null)
            expectedOrigins.Add(EditorDiagnosticOrigin.Compiler);
        var version = session.Begin(expectedPath, source, expectedOrigins);
        if (_styleCopAnalysisCts.TryGetValue(control, out var previous))
            previous.Cancel();
        if (_compilerAnalysisCts.TryGetValue(control, out var previousCompiler))
            previousCompiler.Cancel();

        if (languageServerReady && control.LspDocument is { } currentDocument &&
            currentDocument.Version is { } lspVersion &&
            string.Equals(currentDocument.Text, source, StringComparison.Ordinal) &&
            previousPresentation is { } previousSnapshot &&
            string.Equals(previousSnapshot.Text, source, StringComparison.Ordinal) &&
            previousSnapshot.LanguageServerVersion == lspVersion)
        {
            // 本文もLSP版も同じ場合だけ前回のpush診断を新しい解析回へ引き継ぐ。
            session.Publish(version, EditorDiagnosticOrigin.LanguageServer,
                EditorLspDiagnostics(control), lspVersion);
        }

        if (project is null)
        {
            RefreshStyleCopPresentation(control);
            return;
        }

        var cts = new CancellationTokenSource();
        _styleCopAnalysisCts[control] = cts;
        var openTexts = FindOpenCSharpEditorTexts();

        // 同じ本文の再解析中は安定した表示を保つ。本文が変われば旧範囲を消し、Quick Fixは新しい版を待つ。
        RefreshStyleCopPresentation(control);
        _ = AnalyzeStyleCopAsync(control, project, expectedPath, source, openTexts, version, cts);

        var compilerCts = new CancellationTokenSource();
        var lspOwnsCompilerDiagnostics = control.LspDocument is { IsReady: true } readyDocument &&
            string.Equals(Path.GetFullPath(readyDocument.FilePath), expectedPath, StringComparison.OrdinalIgnoreCase);
        if (lspOwnsCompilerDiagnostics)
        {
            // 接続中のLSP文書がRoslyn compiler診断を返すため、本文変更ごとにCompilationを作り直さない。
            _compilerAnalysisCts.Remove(control);
            compilerCts.Dispose();
        }
        else if (_solutionModel?.Current is { } solution)
        {
            _compilerAnalysisCts[control] = compilerCts;
            _ = AnalyzeCompilerAsync(control, solution, expectedPath, source, openTexts, version, compilerCts);
        }
        else
        {
            compilerCts.Dispose();
        }
    }

    private async Task AnalyzeStyleCopAsync(
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
            // 文字入力中に毎キーでRoslyn Compilationを作らない。最後の入力だけを解析し、
            // 入力が止まった後の診断をLSPと同じく非同期で反映する。
            //
            // ConfigureAwait(false) は必須——このメソッドは UI スレッドから fire-and-forget で
            // 起動されるので、付けないと待機の続き（＝解析本体そのもの）が UI スレッドへ戻り、
            // Roslyn の構文解析とAnalyzer実行が数秒ぶんディスパッチャを占有する。
            // 結果の反映は下の Dispatcher.InvokeAsync で明示的に戻している。
            await Task.Delay(300, cts.Token).ConfigureAwait(false);
            var result = await _styleCopDiagnostics
                .AnalyzeAsync(project, expectedPath, source, cts.Token, openTexts).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_styleCopAnalysisCts.GetValueOrDefault(control), cts) ||
                    !string.Equals(Path.GetFullPath(control.FilePath ?? ""), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(control.Text, source, StringComparison.Ordinal)) return;
                var diagnostics = result.Error is null
                    ? result.Diagnostics
                    : [new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        result.Error, DiagnosticSeverity.Warning, "StyleCop", "LOOMO")];
                GetDiagnosticSession(control).Publish(version, EditorDiagnosticOrigin.StyleCop, diagnostics);
                RefreshStyleCopPresentation(control);
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(_styleCopAnalysisCts.GetValueOrDefault(control), cts))
                {
                    GetDiagnosticSession(control).Publish(version, EditorDiagnosticOrigin.StyleCop, [new LspDiagnostic(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        $"StyleCop解析に失敗しました: {ex.Message}", DiagnosticSeverity.Warning, "StyleCop", "LOOMO")]);
                    RefreshStyleCopPresentation(control);
                }
            });
        }
        finally
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_styleCopAnalysisCts.GetValueOrDefault(control), cts))
                        _styleCopAnalysisCts.Remove(control);
                });
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
            cts.Dispose();
        }
    }

    private async Task AnalyzeCompilerAsync(
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
            // ConfigureAwait(false) の理由は AnalyzeStyleCopAsync 側のコメントと同じ。
            await Task.Delay(300, cts.Token).ConfigureAwait(false);
            var result = await _compilerDiagnostics
                .AnalyzeAsync(solution, expectedPath, source, cts.Token, openTexts).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_compilerAnalysisCts.GetValueOrDefault(control), cts) ||
                    !string.Equals(Path.GetFullPath(control.FilePath ?? ""), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(control.Text, source, StringComparison.Ordinal)) return;
                _compilerUnusedUsingRanges[control] = result.Error is null
                    ? result.UnnecessaryUsingRanges ?? []
                    : [];
                _compilerUnusedUsingVersions[control] = version;
                var diagnostics = result.Error is null
                    ? result.Diagnostics
                    : [new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        result.Error, DiagnosticSeverity.Warning, "Compiler", "LOOMO")];
                GetDiagnosticSession(control).Publish(version, EditorDiagnosticOrigin.Compiler, diagnostics);
                RefreshStyleCopPresentation(control);
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(_compilerAnalysisCts.GetValueOrDefault(control), cts))
                {
                    GetDiagnosticSession(control).Publish(version, EditorDiagnosticOrigin.Compiler, [new LspDiagnostic(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        $"C# compiler解析に失敗しました: {ex.Message}", DiagnosticSeverity.Warning,
                        "Compiler", "LOOMO")]);
                    RefreshStyleCopPresentation(control);
                }
            });
        }
        finally
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_compilerAnalysisCts.GetValueOrDefault(control), cts))
                        _compilerAnalysisCts.Remove(control);
                });
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
            cts.Dispose();
        }
    }

    private void RefreshStyleCopPresentation(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
        {
            ClearStyleCopPresentation(control);
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            control.ReplaceLspDiagnosticPresentation(null);
            control.ReplaceDiagnostics([]);
            return;
        }

        var snapshot = GetDiagnosticSession(control).Presentation;
        if (snapshot is null) return;
        var ranges = _compilerUnusedUsingVersions.GetValueOrDefault(control) == snapshot.Version
            ? _compilerUnusedUsingRanges.GetValueOrDefault(control) ?? []
            : [];
        var entries = ExpandUnnecessaryUsingEntries(snapshot.Entries, ranges);
        var diagnostics = entries.Select(entry => entry.Diagnostic).ToArray();

        // Editorの波線、Problems一覧、Quick Fixの入力を同じ診断スナップショットへ揃える。
        control.ReplaceLspDiagnosticPresentation(diagnostics);
        control.ReplaceDiagnostics([]);
        _vm.Debug.Problems.SetEditorDiagnostics(path, snapshot.Version, entries);
        _vm.TsIde.Problems.SetEditorDiagnostics(path, snapshot.Version, entries);
    }

    private void ClearStyleCopPresentation(VimEditorControl control)
    {
        // Cancel だけでなく辞書からも外す。解析タスク側の finally も同じ掃除をするが、
        // タブを閉じた直後は「Dispose 済みコントロールを鍵にしたエントリ」を残さないことが要点。
        if (_styleCopAnalysisCts.Remove(control, out var cts))
            cts.Cancel();
        if (_compilerAnalysisCts.Remove(control, out var compilerCts))
            compilerCts.Cancel();
        _compilerUnusedUsingRanges.Remove(control);
        _compilerUnusedUsingVersions.Remove(control);
        var ownedPath = "";
        IReadOnlyList<LspDiagnostic> retainedLspDiagnostics = [];
        if (_diagnosticSessions.TryGetValue(control, out var session))
        {
            ownedPath = session.FilePath;
            retainedLspDiagnostics = session.Presentation?.Entries
                .Where(entry => entry.Origin == EditorDiagnosticOrigin.LanguageServer)
                .Select(entry => entry.Diagnostic)
                .ToArray() ?? [];
            session.Clear();
        }
        _diagnosticSessions.Remove(control);
        if (control.FilePath is { Length: > 0 })
        {
            control.ClearDiagnostics();
            control.ReplaceLspDiagnosticPresentation(null);
        }
        if (ownedPath.Length > 0)
        {
            _vm.Debug.Problems.ClearEditorDiagnostics(ownedPath);
            _vm.TsIde.Problems.ClearEditorDiagnostics(ownedPath);
            if (retainedLspDiagnostics.Count > 0)
            {
                var uri = LspUri.FromPath(ownedPath);
                _vm.Debug.Problems.SetLspDiagnostics(uri, retainedLspDiagnostics);
                _vm.TsIde.Problems.SetLspDiagnostics(uri, retainedLspDiagnostics);
            }
        }
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestStyleCopQuickFixesAsync(
        VimEditorControl control,
        LspRange range,
        IReadOnlyList<string>? only,
        EditorDiagnosticSession session,
        EditorDiagnosticSnapshot snapshot)
    {
        if (only is not null && only.Count > 0 &&
            !only.Any(kind =>
                LspCodeActionKinds.Matches(kind, LspCodeActionKinds.QuickFix) ||
                LspCodeActionKinds.Matches(LspCodeActionKinds.QuickFix, kind) ||
                LspCodeActionKinds.Matches(kind, "source.fixAll") ||
                LspCodeActionKinds.Matches("source.fixAll", kind)))
            return [];
        if (control.FilePath is not { Length: > 0 } path)
            return [];

        if (_solutionModel?.Current.ProjectForFile(path) is not { State: ProjectLoadState.Ready } project ||
            !string.Equals(Path.GetFullPath(snapshot.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(control.Text, snapshot.Text, StringComparison.Ordinal) ||
            !session.TryGetCurrent(path, snapshot.Text, out var current) ||
            current.SnapshotId != snapshot.SnapshotId)
            return [];

        // 確定済みスナップショットに含まれるStyleCop診断だけから候補を作る。
        var candidates = snapshot.Entries.Select(entry => entry.Diagnostic)
            .Where(d => d.Code?.StartsWith("SA", StringComparison.OrdinalIgnoreCase) == true)
            .Where(d => IsInRange(d.Range, range)).ToArray();
        if (candidates.Length == 0 || !_styleCopCodeFix.IsAvailable(project))
            return [];

        var actions = new List<LspCodeAction>();
        var version = snapshot.Version;
        var diagnosticSnapshotId = snapshot.SnapshotId;
        var fixClock = Stopwatch.StartNew();
        foreach (var diagnostic in candidates)
        {
            if (session.Version != version || session.SnapshotId != diagnosticSnapshotId ||
                !string.Equals(control.Text, snapshot.Text, StringComparison.Ordinal))
                return [];
            var result = await _styleCopCodeFix.ApplyAsync(project, path, snapshot.Text, diagnostic);
            if (result.Edit is not { } edit || result.Error is not null) continue;
            actions.Add(new LspCodeAction(result.Title ?? $"{diagnostic.Code}を修正",
                LspCodeActionKinds.QuickFix, edit, IsPreferred: true));
        }
        RefactorDebugLog.Write(
            $"quickfix host StyleCop-fix file={path} elapsed={fixClock.ElapsedMilliseconds}ms candidates={candidates.Length} actions={actions.Count}");
        if (session.Version != version || session.SnapshotId != diagnosticSnapshotId ||
            !string.Equals(control.Text, snapshot.Text, StringComparison.Ordinal))
            return [];
        return actions;
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestCSharpQuickFixesAsync(
        VimEditorControl control, LspRange range, IReadOnlyList<string>? only)
    {
        if (only is not null && only.Count > 0 &&
            !only.Any(kind => LspCodeActionKinds.Matches(kind, LspCodeActionKinds.QuickFix) ||
                              LspCodeActionKinds.Matches(LspCodeActionKinds.QuickFix, kind) ||
                              LspCodeActionKinds.Matches(kind, "source.fixAll") ||
                              LspCodeActionKinds.Matches("source.fixAll", kind)))
            return [];
        if (control.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            return [];

        if (only?.Any(kind => LspCodeActionKinds.Matches(kind, "source.fixAll") ||
                              LspCodeActionKinds.Matches("source.fixAll", kind)) == true)
        {
            return await RequestCSharpCompilerFixAllAsync(control, path);
        }

        var source = control.Text;
        var session = EnsureCSharpDiagnosticAnalysisScheduled(control, path, source);
        var version = session.Version;
        var snapshot = session.TryGetCurrent(path, source, out var current) ? current : null;
        if (snapshot is null)
            return [];

        var styleClock = Stopwatch.StartNew();
        var styleCop = await RequestStyleCopQuickFixesAsync(control, range, only, session, snapshot);
        RefactorDebugLog.Write(
            $"quickfix host StyleCop file={path} elapsed={styleClock.ElapsedMilliseconds}ms actions={styleCop.Count}");
        if (session.Version != version || session.SnapshotId != snapshot.SnapshotId ||
            !string.Equals(control.Text, source, StringComparison.Ordinal))
            return [];

        var compilerClock = Stopwatch.StartNew();
        var ranges = _compilerUnusedUsingVersions.GetValueOrDefault(control) == snapshot.Version
            ? _compilerUnusedUsingRanges.GetValueOrDefault(control) ?? []
            : [];
        var diagnostics = ExpandUnnecessaryUsingEntries(snapshot.Entries, ranges)
            .Select(entry => entry.Diagnostic).ToArray();
        var snapshotId = snapshot.SnapshotId;
        var compiler = await sk0ya.Loomo.CSharp.Configuration.CSharpCompilerCodeFixService.GetForDiagnosticsAsync(
            _solutionModel?.Current, path, source, range, diagnostics, only,
            FindOpenCSharpEditorTexts());
        RefactorDebugLog.Write(
            $"quickfix host compiler file={path} elapsed={compilerClock.ElapsedMilliseconds}ms actions={compiler.Count}");
        var suppressionClock = Stopwatch.StartNew();
        var suppressions = snapshot.Entries.Select(entry => entry.Diagnostic).Where(diagnostic => IsInRange(diagnostic.Range, range))
            .GroupBy(diagnostic => $"{diagnostic.Code}|{diagnostic.Range.Start.Line}|{diagnostic.Range.Start.Character}|" +
                                   $"{diagnostic.Range.End.Line}|{diagnostic.Range.End.Character}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .SelectMany(diagnostic => sk0ya.Loomo.CSharp.Configuration.CSharpSuppressionService.Get(
                path, source, diagnostic))
            .ToArray();
        RefactorDebugLog.Write(
            $"quickfix host suppressions file={path} elapsed={suppressionClock.ElapsedMilliseconds}ms actions={suppressions.Length}");
        if (session.Version != version || session.SnapshotId != snapshotId ||
            !string.Equals(control.Text, source, StringComparison.Ordinal))
            return [];
        return styleCop.Concat(compiler).Concat(suppressions).ToArray();
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestCSharpCompilerFixAllAsync(
        VimEditorControl control, string path)
    {
        if (_solutionModel?.Current is not { State: ProjectLoadState.Ready } solution ||
            solution.ProjectForFile(path) is not { State: ProjectLoadState.Ready } project)
            return [];

        var plan = sk0ya.Loomo.CSharp.Refactoring.CSharpFixAllPlanner.CreateForDocument(
            solution, path);
        var result = await sk0ya.Loomo.CSharp.Refactoring.CSharpFixAllService.ApplyAsync(
            solution, plan, FindOpenCSharpEditorTexts());
        if (result.Edit is null || result.Error is { Length: > 0 })
            return [];
        return [new LspCodeAction(
            "C# compilerのFix All",
            "source.fixAll",
            result.Edit,
            IsPreferred: true)];
    }

    private static bool IsInRange(LspRange diagnostic, LspRange requested)
    {
        static int Compare(LspPosition left, LspPosition right)
            => left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Character.CompareTo(right.Character);
        var point = Compare(requested.Start, requested.End) == 0;
        if (point)
            return Compare(diagnostic.Start, requested.Start) <= 0 && Compare(requested.Start, diagnostic.End) <= 0;
        return Compare(diagnostic.Start, requested.End) < 0 && Compare(requested.Start, diagnostic.End) < 0;
    }

    private static EditorDiagnostic ToEditorDiagnostic(LspDiagnostic diagnostic)
        => new(EditorTextRange.Create(diagnostic.Range.Start.Line, diagnostic.Range.Start.Character,
            diagnostic.Range.End.Line, diagnostic.Range.End.Character), diagnostic.Message,
            diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => EditorDiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => EditorDiagnosticSeverity.Warning,
                DiagnosticSeverity.Information => EditorDiagnosticSeverity.Information,
                _ => EditorDiagnosticSeverity.Hint,
            }, diagnostic.Source, diagnostic.Code, null, diagnostic.CodeDescriptionHref,
            diagnostic.Tags?.Select(static tag => tag switch
            {
                DiagnosticTag.Deprecated => EditorDiagnosticTag.Deprecated,
                _ => EditorDiagnosticTag.Unnecessary,
            }).ToArray());

    private EditorDiagnosticSession GetDiagnosticSession(VimEditorControl control)
    {
        if (!_diagnosticSessions.TryGetValue(control, out var session))
            _diagnosticSessions[control] = session = new EditorDiagnosticSession();
        return session;
    }

    private EditorDiagnosticSession EnsureCSharpDiagnosticAnalysisScheduled(
        VimEditorControl control, string filePath, string text)
    {
        var session = GetDiagnosticSession(control);
        var snapshot = session.Presentation;
        if (session.Version == 0 || snapshot is null || snapshot.Version != session.Version ||
            !string.Equals(session.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.Text, text, StringComparison.Ordinal))
        {
            // 入力直後にQuick Fixが開かれても、未開始の版を「診断0件」として確定させない。
            if (Dispatcher.CheckAccess())
                ScheduleStyleCopAnalysis(control);
            else
                Dispatcher.Invoke(() => ScheduleStyleCopAnalysis(control), DispatcherPriority.DataBind);
            session = GetDiagnosticSession(control);
        }
        return session;
    }

    private static IReadOnlyList<EditorDiagnosticEntry> ExpandUnnecessaryUsingEntries(
        IReadOnlyList<EditorDiagnosticEntry> entries, IReadOnlyList<LspRange> individualRanges)
    {
        var expanded = new List<EditorDiagnosticEntry>();
        foreach (var entry in entries)
        {
            var diagnostics = CSharpDiagnosticMerger.ExpandUnnecessaryUsingGroups([entry.Diagnostic], individualRanges);
            expanded.AddRange(diagnostics.Select(diagnostic => entry with { Diagnostic = diagnostic }));
        }
        return expanded;
    }

    private void DisposeCSharpDiagnosticsWiring()
    {
        if (_solutionModel is not null)
            _solutionModel.Changed -= OnCSharpSolutionChanged;
        foreach (var cts in _styleCopAnalysisCts.Values) cts.Cancel();
        foreach (var cts in _styleCopAnalysisCts.Values) cts.Dispose();
        foreach (var cts in _compilerAnalysisCts.Values) cts.Cancel();
        foreach (var cts in _compilerAnalysisCts.Values) cts.Dispose();
        _styleCopAnalysisCts.Clear();
        _compilerAnalysisCts.Clear();
        foreach (var session in _diagnosticSessions.Values) session.Clear();
        _diagnosticSessions.Clear();
        _compilerUnusedUsingRanges.Clear();
        _compilerUnusedUsingVersions.Clear();
        _vm.Debug.Problems.ClearAllEditorDiagnostics();
        _vm.TsIde.Problems.ClearAllEditorDiagnostics();
    }
}
