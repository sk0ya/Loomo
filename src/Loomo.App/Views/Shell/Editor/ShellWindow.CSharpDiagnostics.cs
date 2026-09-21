using System.Collections.Generic;
using System.Linq;
using Editor.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Views;

/// <summary>Loomo.CSharpのStyleCopフォールバックを各C#バッファへ同期する。</summary>
public partial class ShellWindow
{
    private readonly Dictionary<VimEditorControl, EditorDiagnosticSession> _diagnosticSessions = [];
    private CSharpDiagnosticAnalysisController? _diagnosticAnalysis;
    private CSharpDiagnosticPresentationController? _diagnosticPresentation;

    private CSharpDiagnosticAnalysisController DiagnosticAnalysis
        => _diagnosticAnalysis ??= new CSharpDiagnosticAnalysisController(
            Dispatcher, _styleCopDiagnostics, _compilerDiagnostics,
            GetDiagnosticSession, RefreshStyleCopPresentation,
            path => _solutionModel?.ProjectForFile(path),
            () => _solutionModel?.Current, FindOpenCSharpEditorTexts);

    private CSharpDiagnosticPresentationController DiagnosticPresentation
        => _diagnosticPresentation ??= new CSharpDiagnosticPresentationController(
            _diagnosticSessions, _vm.Debug.Problems, _vm.TsIde.Problems,
            control => DiagnosticAnalysis.Cancel(control));
    private CSharpEditorQuickFixCoordinator? _quickFixCoordinator;

    private CSharpEditorQuickFixCoordinator QuickFixCoordinator
        => _quickFixCoordinator ??= new CSharpEditorQuickFixCoordinator(
            () => _solutionModel?.Current, _styleCopCodeFix,
            EnsureCSharpDiagnosticAnalysisScheduled, FindOpenCSharpEditorTexts);

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
        // ここで版を「本文に合わせるだけ」にしてはいけない。解析を一つも待たない版は<b>診断0件のまま確定</b>し、
        // 走っている解析の結果まで版違いで捨ててしまう。ずれていたら解析ごと開始し直すのが正しい。
        var session = EnsureCSharpDiagnosticAnalysisScheduled(control, fullPath, control.Text);
        if (!DiagnosticAnalysis.TryPublishLanguageServerDiagnostics(control, session))
            return;
    }

    private void ScheduleStyleCopAnalysis(VimEditorControl control)
        => DiagnosticAnalysis.Schedule(control, ClearStyleCopPresentation);

    private void RefreshStyleCopPresentation(VimEditorControl control)
        => DiagnosticPresentation.Refresh(control);

    private void ClearStyleCopPresentation(VimEditorControl control)
        => DiagnosticPresentation.Clear(control);

    private EditorDiagnosticSession GetDiagnosticSession(VimEditorControl control)
        => DiagnosticPresentation.GetSession(control);

    private EditorDiagnosticSession EnsureCSharpDiagnosticAnalysisScheduled(
        VimEditorControl control, string filePath, string text)
        => DiagnosticAnalysis.EnsureScheduled(control, filePath, text, ScheduleStyleCopAnalysis);

    private void DisposeCSharpDiagnosticsWiring()
    {
        if (_solutionModel is not null)
            _solutionModel.Changed -= OnCSharpSolutionChanged;
        DiagnosticAnalysis.CancelAll();
        DiagnosticPresentation.ClearAll();
    }
}
