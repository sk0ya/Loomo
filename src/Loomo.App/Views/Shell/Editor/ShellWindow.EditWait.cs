using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: ターミナルの <c>EDITOR</c> を部屋のエディタにする（<c>code --wait</c> 相当、§38.5）。
/// Claude Code の Ctrl+G（プロンプトを外部エディタで書く）や <c>git commit</c> がファイルを開くと、Loomo のエディタの
/// タブになり、そのタブを閉じたら呼び出し元へ戻る。<c>EDITOR</c>／<c>VISUAL</c> は、Loomo を起動した環境に
/// 既に設定があれば上書きしない（人が選んだエディタが優先）。
/// </summary>
public partial class ShellWindow {
    private LoomoEditBridge? _editBridge;
    private string? _editorShim;
    private readonly Dictionary<string, List<TaskCompletionSource>> _editWaiters = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeEditWait() {
        try {
            _editorShim = LoomoEditBridge.InstallScripts();
            _editBridge = new LoomoEditBridge(path => Dispatcher.InvokeAsync(() => OpenAndWaitForCloseAsync(path)).Task.Unwrap());
            _editBridge.Start();
        } catch (Exception ex) {
            System.Diagnostics.Trace.WriteLine($"EDITOR の受け口を開けませんでした: {ex.Message}");
            _editBridge = null;
        }
    }
    private void DisposeEditWait() {
        _editBridge?.Dispose();
        foreach (var waiters in _editWaiters.Values)
            foreach (var waiter in waiters)
                waiter.TrySetResult();   // 閉じるなら待っている側を放す（メモ帳へは落ちない。書いた分はディスクにある）
        _editWaiters.Clear();
    }

    /// <summary>ターミナルへ渡す EDITOR 関係の環境変数。</summary>
    private void AddEditWaitEnvironment(Dictionary<string, string> environment) {
        if (_editBridge is null || _editorShim is null)
            return;
        environment["LOOMO_EDIT_PIPE"] = _editBridge.PipeName;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDITOR")))
            environment["EDITOR"] = _editorShim;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VISUAL")))
            environment["VISUAL"] = _editorShim;
    }

    private async Task OpenAndWaitForCloseAsync(string path) {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_editWaiters.TryGetValue(path, out var list))
            _editWaiters[path] = list = [];
        list.Add(waiter);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await OpenFileInNewEditorTabAsync(path);
        FocusPane(PaneKind.Editor);
        if (_activeEditorTab is { IsRealized: true } tab && FilePathRelations.AreEqual(tab.PeekFilePath ?? "", path))
            tab.Control.ShowStatusMessage("書き終えたら保存してタブを閉じる（:wq）と、呼び出し元（Claude Code・git など）へ戻ります");
        await waiter.Task;
    }

    /// <summary>エディタのタブが閉じた。そのファイルを待っている EDITOR 呼び出しを放す。</summary>
    private void ReleaseEditWaiters(string closedPath) {
        if (_editWaiters.Count == 0)
            return;
        var key = _editWaiters.Keys.FirstOrDefault(path => FilePathRelations.AreEqual(path, closedPath));
        if (key is null || !_editWaiters.Remove(key, out var waiters))
            return;
        foreach (var waiter in waiters)
            waiter.TrySetResult();
        // タブを外し終えて次のタブが前に出たあとで、呼び出し元のターミナルへ戻す。
        Dispatcher.BeginInvoke(() => FocusPane(PaneKind.Terminal), System.Windows.Threading.DispatcherPriority.Background);
    }
}
