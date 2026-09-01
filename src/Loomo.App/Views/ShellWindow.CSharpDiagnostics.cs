using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Editor.Controls;
using Editor.Controls.HostIntegration;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Views;

/// <summary>Loomo.CSharpのStyleCopフォールバックを各C#バッファへ同期する。</summary>
public partial class ShellWindow
{
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _styleCopAnalysisCts = [];
    private readonly Dictionary<VimEditorControl, IReadOnlyList<LspDiagnostic>> _styleCopResults = [];
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _compilerAnalysisCts = [];
    private readonly Dictionary<VimEditorControl, IReadOnlyList<LspDiagnostic>> _compilerResults = [];

    private static IReadOnlyList<LspDiagnostic> EditorLspDiagnostics(VimEditorControl control)
    {
#if LOOMO_EDITOR_HOST_API
        return control.LspDiagnostics;
#else
        return [];
#endif
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
        if (sender is VimEditorControl control)
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
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase) ||
            _solutionModel?.ProjectForFile(path) is not { } project)
        {
            ClearStyleCopPresentation(control);
            return;
        }

        if (_styleCopAnalysisCts.TryGetValue(control, out var previous))
            previous.Cancel();
        var cts = new CancellationTokenSource();
        _styleCopAnalysisCts[control] = cts;
        var source = control.Text;
        var expectedPath = Path.GetFullPath(path);
        var openTexts = FindOpenCSharpEditorTexts();

        // 新しい本文に対して解析中は、前の本文の診断／Quick Fixを残さない。
        // LSP側の最新診断は保持し、fallback側だけを空にして「解析中」と一致させる。
        _styleCopResults.Remove(control);
        _compilerResults.Remove(control);
        RefreshStyleCopPresentation(control);
        _ = AnalyzeStyleCopAsync(control, project, expectedPath, source, openTexts, cts);

        if (_compilerAnalysisCts.TryGetValue(control, out var previousCompiler))
            previousCompiler.Cancel();
        var compilerCts = new CancellationTokenSource();
        _compilerAnalysisCts[control] = compilerCts;
        if (_solutionModel?.Current is { } solution)
            _ = AnalyzeCompilerAsync(control, solution, expectedPath, source, openTexts, compilerCts);
    }

    private async Task AnalyzeStyleCopAsync(
        VimEditorControl control,
        ProjectModel project,
        string expectedPath,
        string source,
        IReadOnlyDictionary<string, string> openTexts,
        CancellationTokenSource cts)
    {
        try
        {
            // 文字入力中に毎キーでRoslyn Compilationを作らない。最後の入力だけを解析し、
            // 入力が止まった後の診断をLSPと同じく非同期で反映する。
            await Task.Delay(300, cts.Token);
            var result = await _styleCopDiagnostics.AnalyzeAsync(project, expectedPath, source, cts.Token, openTexts);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_styleCopAnalysisCts.GetValueOrDefault(control), cts) ||
                    !string.Equals(Path.GetFullPath(control.FilePath ?? ""), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(control.Text, source, StringComparison.Ordinal)) return;
                _styleCopResults[control] = result.Error is null
                    ? result.Diagnostics
                    : [new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        result.Error, DiagnosticSeverity.Warning, "StyleCop", "LOOMO")];
                RefreshStyleCopPresentation(control);
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(_styleCopAnalysisCts.GetValueOrDefault(control), cts))
                    _vm.Debug.Problems.SetStyleCopDiagnostics(expectedPath, [new LspDiagnostic(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        $"StyleCop解析に失敗しました: {ex.Message}", DiagnosticSeverity.Warning, "StyleCop", "LOOMO")]);
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
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(300, cts.Token);
            var result = await _compilerDiagnostics.AnalyzeAsync(solution, expectedPath, source, cts.Token, openTexts);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_compilerAnalysisCts.GetValueOrDefault(control), cts) ||
                    !string.Equals(Path.GetFullPath(control.FilePath ?? ""), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(control.Text, source, StringComparison.Ordinal)) return;
                _compilerResults[control] = result.Error is null
                    ? result.Diagnostics
                    : [new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        result.Error, DiagnosticSeverity.Warning, "Compiler", "LOOMO")];
                RefreshStyleCopPresentation(control);
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(_compilerAnalysisCts.GetValueOrDefault(control), cts))
                    _vm.Debug.Problems.SetCompilerDiagnostics(expectedPath, [new LspDiagnostic(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        $"C# compiler解析に失敗しました: {ex.Message}", DiagnosticSeverity.Warning,
                        "Compiler", "LOOMO")]);
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

        // LSPが同じ種類の診断を1件でも返したからといって、fallback全体を捨てない。
        // サーバーが部分的な診断しか返さない場合は、同じcode／rangeだけを除外して
        // 残りの公式Analyzer／compiler診断を併記する。Problems側の重複排除も通るが、
        // Editorの波線・gutterには重複を送らない。
        var lspDiagnostics = EditorLspDiagnostics(control);
        var fallbackStyleCop = CSharpDiagnosticMerger.ExcludeDuplicates(
            lspDiagnostics, _styleCopResults.GetValueOrDefault(control) ?? []);
        var fallbackCompiler = CSharpDiagnosticMerger.ExcludeDuplicates(
            lspDiagnostics, _compilerResults.GetValueOrDefault(control) ?? []);
        _vm.Debug.Problems.SetStyleCopDiagnostics(path, fallbackStyleCop);
        _vm.Debug.Problems.SetCompilerDiagnostics(path, fallbackCompiler);

        var fallbackDiagnostics = fallbackStyleCop.Concat(fallbackCompiler).Select(ToEditorDiagnostic);
        control.ReplaceDiagnostics(fallbackDiagnostics);
    }

    private void ClearStyleCopPresentation(VimEditorControl control)
    {
        // Cancel だけでなく辞書からも外す。解析タスク側の finally も同じ掃除をするが、
        // タブを閉じた直後は「Dispose 済みコントロールを鍵にしたエントリ」を残さないことが要点。
        if (_styleCopAnalysisCts.Remove(control, out var cts))
            cts.Cancel();
        if (_compilerAnalysisCts.Remove(control, out var compilerCts))
            compilerCts.Cancel();
        _styleCopResults.Remove(control);
        _compilerResults.Remove(control);
        if (control.FilePath is { Length: > 0 } path)
        {
            control.ClearDiagnostics();
            _vm.Debug.Problems.ClearStyleCopDiagnostics(path);
            _vm.Debug.Problems.ClearCompilerDiagnostics(path);
        }
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestStyleCopQuickFixesAsync(
        VimEditorControl control, LspRange range, IReadOnlyList<string>? only)
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

        // 起動直後はSolution Explorerが表示済みでもMSBuild評価が継続していることがある。
        // Quick Fixをその瞬間の「候補なし」で終わらせず、短時間だけ評価完了を待つ。
        ProjectModel? project = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            project = _solutionModel?.Current.ProjectForFile(path);
            if (project is { State: ProjectLoadState.Ready }) break;
            if (attempt < 39) await Task.Delay(250);
        }
        if (project is not { State: ProjectLoadState.Ready })
            return [];

        // StyleCop の診断は、LSP が返す環境と Loomo.CSharp のフォールバックが返す環境がある。
        // CodeFix はどちらの診断でも同じ公式 StyleCop DLLへ委譲できるため、Quick Fixでは
        // 両方を候補源にする（表示時の重複は診断マージ側で抑制される）。
        var diagnostics = (_styleCopResults.GetValueOrDefault(control) ?? [])
            .Concat(EditorLspDiagnostics(control))
            .GroupBy(d => $"{d.Code}|{d.Range.Start.Line}|{d.Range.Start.Character}|" +
                          $"{d.Range.End.Line}|{d.Range.End.Character}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var candidates = diagnostics.Where(d => d.Code?.StartsWith("SA", StringComparison.OrdinalIgnoreCase) == true)
            .Where(d => IsInRange(d.Range, range)).ToArray();

        // 解析のdebounce中にユーザーがQuick Fixを押しても、キャッシュが空のまま
        // 「候補なし」にならないよう、StyleCop診断をその場の本文で再確認する。
        // 公式Analyzerを正本にするため、ここでもルールを再実装しない。
        if (candidates.Length == 0 && _styleCopCodeFix.IsAvailable(project))
        {
            var analysis = await _styleCopDiagnostics.AnalyzeAsync(
                project, path, control.Text, cancellationToken: default,
                openTexts: FindOpenCSharpEditorTexts());
            if (analysis.Error is null)
            {
                _styleCopResults[control] = analysis.Diagnostics;
                RefreshStyleCopPresentation(control);
                candidates = analysis.Diagnostics
                    .Where(d => d.Code?.StartsWith("SA", StringComparison.OrdinalIgnoreCase) == true)
                    .Where(d => IsInRange(d.Range, range)).ToArray();
            }
        }
        if (candidates.Length == 0) return [];

        var actions = new List<LspCodeAction>();
        foreach (var diagnostic in candidates)
        {
            var result = await _styleCopCodeFix.ApplyAsync(project, path, control.Text, diagnostic);
            if (result.Edit is not { } edit || result.Error is not null) continue;
            actions.Add(new LspCodeAction(result.Title ?? $"{diagnostic.Code}を修正",
                LspCodeActionKinds.QuickFix, edit, IsPreferred: true));
        }
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

        var styleCop = await RequestStyleCopQuickFixesAsync(control, range, only);
        var compiler = await sk0ya.Loomo.CSharp.Configuration.CSharpCompilerCodeFixService.GetAsync(
            _solutionModel?.Current, path, control.Text, range, only,
            FindOpenCSharpEditorTexts());
        var suppressions = (_styleCopResults.GetValueOrDefault(control) ?? [])
            .Concat(_compilerResults.GetValueOrDefault(control) ?? [])
            .Concat(EditorLspDiagnostics(control))
            .Where(diagnostic => IsInRange(diagnostic.Range, range))
            .GroupBy(diagnostic => $"{diagnostic.Code}|{diagnostic.Range.Start.Line}|{diagnostic.Range.Start.Character}|" +
                                   $"{diagnostic.Range.End.Line}|{diagnostic.Range.End.Character}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .SelectMany(diagnostic => sk0ya.Loomo.CSharp.Configuration.CSharpSuppressionService.Get(
                path, control.Text, diagnostic));
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
            }, diagnostic.Source, diagnostic.Code);

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
        _styleCopResults.Clear();
        _compilerResults.Clear();
        _vm.Debug.Problems.ClearAllStyleCopDiagnostics();
        _vm.Debug.Problems.ClearAllCompilerDiagnostics();
    }
}
