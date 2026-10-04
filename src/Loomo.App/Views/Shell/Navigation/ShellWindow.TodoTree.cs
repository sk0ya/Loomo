namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private readonly SemaphoreSlim _todoNavigationGate = new(1, 1);
    private int _todoNavigationVersion;

    /// <summary>連続選択を直列化し、遅れたプレビューが新しい選択の行位置を上書きしないようにする。</summary>
    private async Task NavigateTodoAsync(ContentSearchHit hit, bool preview)
    {
        var version = ++_todoNavigationVersion;
        var keepFocus = preview && SidebarTodoTree.IsKeyboardFocusWithin;
        await _todoNavigationGate.WaitAsync();
        try
        {
            if (version != _todoNavigationVersion || !_workspace.Contains(hit.FullPath)) return;
            if (!File.Exists(hit.FullPath))
            {
                ToastService.Error("ファイルが見つかりません。TODO 一覧を更新してください。");
                return;
            }
            if (preview) await OpenFileInPreviewTabAsync(hit.FullPath);
            else await OpenFileInNewEditorTabAsync(hit.FullPath);
            if (version != _todoNavigationVersion || !_workspace.Contains(hit.FullPath)
                || !string.Equals(_activeEditorTab?.Control.FilePath, hit.FullPath, StringComparison.OrdinalIgnoreCase)) return;
            _activeEditorTab?.Control.NavigateTo(hit.Line - 1, Math.Max(0, hit.Column - 1));
            if (keepFocus && SidebarTodoTree.IsVisible && _vm.TodoTree?.SelectedEntry?.Hit == hit)
                SidebarTodoTree.FocusSelectedEntry();
        }
        catch (Exception ex) { ToastService.Error($"TODO を開けませんでした: {ex.Message}"); }
        finally { _todoNavigationGate.Release(); }
    }
}
