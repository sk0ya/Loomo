
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 本文中のリンク／ファイルパスのクリック（エディタ・ターミナルの URL/ファイル、
/// OSC8 ハイパーリンク）を内蔵ブラウザペインやエディタタブで開く振り分け。</summary>
public partial class ShellWindow {
    private async Task OpenFileInBrowserAsync(string path) {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        await OpenUrlInBrowserAsync(new Uri(Path.GetFullPath(path)).AbsoluteUri, Path.GetFileName(path));
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
        if (!EditorFileLinkResolver.TryResolve( e.Path, currentPath, _workspace.RootPath, out var fullPath, out var line, out var column, out var isDirectory)) {
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
        var target = e.Target;
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) {
                e.Handled = true;
                _ = OpenUrlInBrowserAsync(uri.AbsoluteUri, null);
                return;
            }

            if (uri.IsFile) {
                e.Handled = true;
                _ = OpenPathInEditorAsync(uri.LocalPath, line: 0, column: 0);
                return;
            }

            return; // mailto: 等は既定の外部起動に委ねる。
        }

        var cwd = (sender as TerminalTabView)?.WorkingDirectory;
        if (TryResolveFilePath(target, cwd, out var fullPath, out var line, out var column)) {
            e.Handled = true;
            _ = OpenPathInEditorAsync(fullPath, line, column);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TrailingLineColumn =
        new(@":(\d+)(?::(\d+))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TryResolveFilePath(string target, string? workingDirectory, out string fullPath, out int line, out int column) {
        fullPath = "";
        line = 0;
        column = 0;

        var path = target;
        var match = TrailingLineColumn.Match(path);
        if (match.Success)
        {
            path = path[..match.Index];
            int.TryParse(match.Groups[1].Value, out line);
            if (match.Groups[2].Success)
                int.TryParse(match.Groups[2].Value, out column);
        }

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try {
            if (!Path.IsPathRooted(path)) {
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    return false;
                path = Path.Combine(workingDirectory, path);
            }

            fullPath = Path.GetFullPath(path);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }

        return File.Exists(fullPath);
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

    private async Task HandleEditorSupportLinkClickedAsync(string href, string? sourcePath = null) {
        if (string.IsNullOrWhiteSpace(href))
            return;

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri)) {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) {
                await OpenUrlInBrowserAsync(uri.AbsoluteUri, null);
                return;
            }

            if (uri.IsFile) {
                await OpenPathInEditorAsync(uri.LocalPath, line: 0, column: 0);
                return;
            }

            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* 開けるハンドラが無い等でも落とさない。 */ }
            return;
        }

        var currentPath = sourcePath ?? _editorSupport.Source?.Control.FilePath;
        if (!EditorFileLinkResolver.TryResolve( href, currentPath, _workspace.RootPath, out var fullPath, out var line, out var column, out var isDirectory))
            return;

        if (isDirectory) {
            _workspace.SelectedPath = fullPath;
            return;
        }

        await OpenPathInEditorAsync(fullPath, line, column);
    }

    private async Task OpenUrlInBrowserAsync(string url, string? title) {
        if (string.IsNullOrWhiteSpace(url))
            return;

        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);

        await CreateBrowserTabAsync(url, requestedTitle: title);
        SaveActiveWorkspaceSnapshot();
    }
}
