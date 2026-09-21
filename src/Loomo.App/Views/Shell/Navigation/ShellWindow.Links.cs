using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: 本文中のリンク／ファイルパスのクリック（エディタ・ターミナルの URL/ファイル、 OSC8 ハイパーリンク）を内蔵ブラウザペインやエディタタブで開く振り分け。</summary>
public partial class ShellWindow {
    private async Task OpenFileInBrowserAsync(string path) {
        if (FileBrowserLinkTargetResolver.TryResolve(path, out var target))
            await OpenUrlInBrowserAsync(target.Url, target.Title);
    }
    private void OnEditorLinkClicked(object? sender, LinkClickedEventArgs e) {
        if (string.IsNullOrWhiteSpace(e.Url))
            return;
        e.Handled = true;
        _ = OpenUrlInBrowserAsync(e.Url, null);
    }
    private void OnEditorFileLinkClicked(object? sender, FileLinkClickedEventArgs e) {
        if (string.IsNullOrWhiteSpace(e.Path))
            return;
        var currentPath = (sender as VimEditorControl)?.FilePath;
        if (!FileLinkResolver.TryResolve( _workspace, e.Path, currentPath, out var fullPath, out var line, out var column, out var isDirectory)) {
            e.Handled = true;
            if (sender is VimEditorControl editor)
                editor.ShowStatusMessage($"ファイルが存在しません: {e.Path}");
            return;
        }
        e.Handled = true;
        if (isDirectory) {
            _workspace.SelectedPath = fullPath;
            return;
        }
        _ = OpenPathInEditorAsync(fullPath, line, column);
    }
    private void OnTerminalLinkActivated(object? sender, TerminalHyperlinkActivatedEventArgs e) {
        var target = TerminalLinkTargetResolver.Resolve(
            _workspace, e.Target, (sender as TerminalTabView)?.WorkingDirectory);
        switch (target.Kind) {
            case LinkOpenTargetKind.Url:
                e.Handled = true;
                _ = OpenUrlInBrowserAsync(target.Value, null);
                break;
            case LinkOpenTargetKind.File:
                e.Handled = true;
                _ = OpenTerminalPathAsync(target.Value, target.Line, target.Column);
                break;
        }
    }
    /// <summary>ターミナルからのリンクは、開くだけでなく着地点の Editor ペインへ移す。</summary>
    private async Task OpenTerminalPathAsync(string path, int line, int column)
    {
        if (!File.Exists(path))
            return;
        await OpenPathInEditorAsync(path, line, column);
        FocusPane(PaneKind.Editor);
    }

    private async Task OpenPathInEditorAsync(string fullPath, int line, int column, bool alignTop = false) {
        await OpenFileInNewEditorTabAsync(fullPath);
        if (line <= 0)
            return;
        if (_activeEditorTab is { } tab && string.Equals(tab.PeekFilePath, fullPath, StringComparison.OrdinalIgnoreCase)) {
            tab.Control.NavigateTo(line - 1, column > 0 ? column - 1 : 0);
            if (alignTop)
                tab.Control.ScrollCursorToTop();
        }
    }
    private Task HandleEditorSupportLinkClickedAsync(string href, string? sourcePath = null)
        => EditorSupportLinkController.OpenAsync(
            _workspace,
            href,
            sourcePath ?? _editorSupport.Source?.Control.FilePath,
            url => OpenUrlInBrowserAsync(url, null),
            (path, line, column) => OpenPathInEditorAsync(path, line, column),
            path => _workspace.SelectedPath = path,
            uri => {
                try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
                catch { /* 開けるハンドラが無い等でも落とさない。 */ }
            });
    private async Task OpenUrlInBrowserAsync(string url, string? title) {
        if (string.IsNullOrWhiteSpace(url))
            return;
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        await CreateBrowserTabAsync(url, requestedTitle: title);
        SaveActiveWorkspaceSnapshot();
    }
}
