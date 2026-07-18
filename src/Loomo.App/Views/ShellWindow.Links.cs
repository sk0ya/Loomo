
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 本文中のリンク／ファイルパスのクリック（エディタ・ターミナルの URL/ファイル、
/// OSC8 ハイパーリンク）を内蔵ブラウザペインやエディタタブで開く振り分け。</summary>
public partial class ShellWindow
{
    // FolderTree の HTML をアプリ内ブラウザの新規タブで開く。
    private async Task OpenFileInBrowserAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        await OpenUrlInBrowserAsync(new Uri(Path.GetFullPath(path)).AbsoluteUri, Path.GetFileName(path));
    }

    // エディタ本文の URL クリック（Ctrl+Click / gx）を、OS 既定ブラウザではなく内蔵ブラウザペインで開く。
    private void OnEditorLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Url))
            return;

        // 既定動作（Process.Start で OS のブラウザを開く）を抑止し、内蔵ブラウザで開く。
        e.Handled = true;
        _ = OpenUrlInBrowserAsync(e.Url, null);
    }

    // エディタ本文のファイルパスクリック（Ctrl+Click / gx）を、現在ファイルまたはワークスペースを 基準に解決して Loomo のエディタタブで開く。
    private void OnEditorFileLinkClicked(object? sender, FileLinkClickedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Path))
            return;

        var currentPath = (sender as VimEditorControl)?.FilePath;
        if (!EditorFileLinkResolver.TryResolve(
                e.Path,
                currentPath,
                _workspace.RootPath,
                out var fullPath,
                out var line,
                out var column,
                out var isDirectory))
        {
            e.Handled = true;
            if (sender is VimEditorControl editor)
                editor.ShowStatusMessage($"ファイルが存在しません: {e.Path}");
            return;
        }

        e.Handled = true;
        if (isDirectory)
        {
            _workspace.SelectedPath = fullPath;
            return;
        }

        _ = OpenPathInEditorAsync(fullPath, line, column);
    }

    // ターミナル本文のクリック（OSC 8 ハイパーリンク／検出した URL・ファイルパス）を Loomo で受け、 振り分ける（sk0ya.Terminal.Controls 1.0.12 は生テキスト Target を渡してくる）。 http/https は内蔵ブラウザペインで、ファイルパス（必要なら :行[:列] 付き）はエディタで開く。 それ以外（mailto: 等や、解決できないファイルパス）は Handled=false のままにして、 ライブラリ既定の外部起動（Process.Start）に委ねる。
    private void OnTerminalLinkActivated(object? sender, TerminalHyperlinkActivatedEventArgs e)
    {
        var target = e.Target;
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                // 既定動作（OS ブラウザ）を抑止し、内蔵ブラウザで開く。
                e.Handled = true;
                _ = OpenUrlInBrowserAsync(uri.AbsoluteUri, null);
                return;
            }

            if (uri.IsFile)
            {
                e.Handled = true;
                _ = OpenPathInEditorAsync(uri.LocalPath, line: 0, column: 0);
                return;
            }

            return; // mailto: 等は既定の外部起動に委ねる。
        }

        // 絶対 URI でなければファイルパスとして扱う（:行[:列] 付きを許容し、ターミナルの cwd で解決）。
        var cwd = (sender as TerminalTabView)?.WorkingDirectory;
        if (TryResolveFilePath(target, cwd, out var fullPath, out var line, out var column))
        {
            e.Handled = true;
            _ = OpenPathInEditorAsync(fullPath, line, column);
        }
        // 解決できなければ Handled=false のまま → ライブラリ既定の Process.Start に委ねる。
    }

    private static readonly System.Text.RegularExpressions.Regex TrailingLineColumn =
        new(@":(\d+)(?::(\d+))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ターミナルが検出したファイルパス文字列（末尾に :行[:列] が付くことがある）を、絶対パスと 行・列に分解する。相対パスは workingDirectory（ターミナルの cwd）で解決し、 実在するファイルのときだけ true を返す。
    private static bool TryResolveFilePath(string target, string? workingDirectory, out string fullPath, out int line, out int column)
    {
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

        try
        {
            if (!Path.IsPathRooted(path))
            {
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    return false;
                path = Path.Combine(workingDirectory, path);
            }

            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return File.Exists(fullPath);
    }

    // ファイルをエディタの新規タブで開き、行・列が指定されていればそこへキャレットを移動する。
    private async Task OpenPathInEditorAsync(string fullPath, int line, int column, bool alignTop = false)
    {
        await OpenFileInNewEditorTabAsync(fullPath);

        if (line <= 0)
            return;

        // 開いたタブがアクティブになっているので、そのコントロールでキャレットを移動する。
        if (_activeEditorTab is { } tab &&
            string.Equals(tab.PeekFilePath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            // line/column は1始まり、NavigateTo は0始まりなので変換する。
            tab.Control.NavigateTo(line - 1, column > 0 ? column - 1 : 0);
            // コード構造の②パネルからのジャンプは、対象行を vim の zt 相当でビュー最上段へ寄せる。
            if (alignTop)
                tab.Control.ScrollCursorToTop();
        }
    }

    // Markdown プレビュー本文のリンククリック（<a href>）を振り分ける。http/https は内蔵ブラウザペイン、 ファイルパス（相対はプレビュー元ファイルのフォルダ→ワークスペース根の順で解決、:行[:列] も許容）は エディタタブで開く。それ以外（mailto: 等）は OS 既定の外部起動に委ねる。解決できないリンクは何もしない。 sourcePath は相対リンクの基準にするプレビュー元ファイル。省略時は EditorSupport ペインの追従元タブ（別ウィンドウの複製は自身の追従元を渡す）。
    private async Task HandleEditorSupportLinkClickedAsync(string href, string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(href))
            return;

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                await OpenUrlInBrowserAsync(uri.AbsoluteUri, null);
                return;
            }

            if (uri.IsFile)
            {
                await OpenPathInEditorAsync(uri.LocalPath, line: 0, column: 0);
                return;
            }

            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* 開けるハンドラが無い等でも落とさない。 */ }
            return;
        }

        var currentPath = sourcePath ?? _editorSupportSourceTab?.Control.FilePath;
        if (!EditorFileLinkResolver.TryResolve(
                href, currentPath, _workspace.RootPath, out var fullPath, out var line, out var column, out var isDirectory))
            return;

        if (isDirectory)
        {
            _workspace.SelectedPath = fullPath;
            return;
        }

        await OpenPathInEditorAsync(fullPath, line, column);
    }

    // 任意の URL をアプリ内ブラウザの新規タブで開く（必要ならブラウザペインを表示する）。
    private async Task OpenUrlInBrowserAsync(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // ブラウザペインがレイアウトに無ければ左上と入れ替えて前面に出す（「ブラウザで調べる」等と
        // 同じ流儀＝最下段への新規挿入ではなく入れ替えで一貫させる。既に見えていれば何もしない）。
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);

        await CreateBrowserTabAsync(url, requestedTitle: title);
        SaveActiveWorkspaceSnapshot();
    }
}
