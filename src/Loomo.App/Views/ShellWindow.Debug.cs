using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// デバッグ（DAP）とエディタの橋渡し。エディタのブレークポイント列のトグルを
/// <see cref="ViewModels.DebugViewModel"/> のブレークポイントストア＋デバッグアダプタへ反映し、
/// 停止位置（実行中行）をエディタのハイライトへ反映する。
/// ブレークポイント状態の真実は DebugViewModel が持ち、エディタは表示するだけ（AI/人間で経路を一本化する流儀）。
/// </summary>
public partial class ShellWindow
{
    /// <summary>ctor から一度だけ呼び、停止位置→実行行ハイライトの配線をつなぐ。
    /// デバッグペインの開閉はタイトルバーのペイントグル（他ペインと同じ <c>OnTogglePaneVisibility</c>）が担う。
    /// アダプタ未導入の再確認は未導入バーの「再確認」ボタンで行う。</summary>
    private void InitializeDebugWiring()
    {
        _vm.Debug.ExecutionLineChanged += OnDebugExecutionLineChanged;
    }

    /// <summary>新規エディタにブレークポイント列を有効化し、トグル/同期を配線する（BuildEditorControl から呼ぶ）。</summary>
    private void WireEditorForDebug(VimEditorControl control)
    {
        control.SetBreakpointsEnabled(true);
        control.BreakpointToggled += line => OnEditorBreakpointToggled(control, line);
        // ファイル読込・差し替え時に、そのパスのブレークポイントをエディタへ反映する。
        control.BufferChanged += (_, _) => SyncEditorBreakpoints(control);
        SyncEditorBreakpoints(control);
    }

    private void OnEditorBreakpointToggled(VimEditorControl control, int line0)
    {
        var path = control.FilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var lines = _vm.Debug.ToggleBreakpoint(path, line0);
        control.SetBreakpoints(lines);
    }

    private void SyncEditorBreakpoints(VimEditorControl control)
    {
        var path = control.FilePath;
        control.SetBreakpoints(string.IsNullOrWhiteSpace(path)
            ? Array.Empty<int>()
            : _vm.Debug.GetBreakpoints(path));
    }

    /// <summary>停止位置をエディタへ反映する。まず全エディタの実行行を解除し、対象パスのエディタを
    /// （無ければ開いて）見つけてその行をハイライトする。path=null/line&lt;0 は解除のみ。</summary>
    private async void OnDebugExecutionLineChanged(string? path, int line0)
    {
        foreach (var c in RealizedEditorControls())
            c.SetExecutionLine(-1);

        if (string.IsNullOrWhiteSpace(path) || line0 < 0) return;

        var control = FindEditorControl(path);
        if (control is null)
        {
            await OpenFileInNewEditorTabAsync(path);
            control = FindEditorControl(path);
        }
        control?.SetExecutionLine(line0);
    }

    private IEnumerable<VimEditorControl> RealizedEditorControls()
        => _editorTabs.Where(t => t.IsRealized).Select(t => t.Control);

    private VimEditorControl? FindEditorControl(string path)
    {
        var full = Path.GetFullPath(path);
        return RealizedEditorControls().FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.FilePath) &&
            string.Equals(Path.GetFullPath(c.FilePath), full, StringComparison.OrdinalIgnoreCase));
    }
}
