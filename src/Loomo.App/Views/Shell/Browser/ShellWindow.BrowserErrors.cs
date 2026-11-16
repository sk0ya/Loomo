using sk0ya.Loomo.App.Services.Browser;
using sk0ya.Loomo.Services.Browser;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザのページのエラーを Claude が読む（§45.5 P2 `browser_errors`）。読むのは<b>ローカルのページだけ</b>
/// （localhost・Loomo のファイル・ワークスペースのファイル）。ほかのサイトは、サインインしていることがあるので読まない。
/// </summary>
public partial class ShellWindow {
    /// <summary>ブラウザペインの前に出ているページのエラーと警告（UI スレッドで読む）。</summary>
    internal LoomoRoomResult DescribeBrowserErrors() {
        var core = ActiveBrowserView.TryCore();
        if (core is null)
            return new LoomoRoomResult(true, "The browser pane has no page in front (nothing to read).");
        var url = core.Source;
        if (!BrowserConsoleLog.IsLocalPage(url))
            return new LoomoRoomResult(true,
                "The page in the browser pane is not a local page (localhost, Loomo's files or the workspace). "
                + "Loomo does not read other sites' pages.");
        var entries = BrowserConsoleWatch.LogFor(core).Recent(10);
        if (entries.Count == 0)
            return new LoomoRoomResult(true, $"Page: {url}\nNo errors or warnings recorded since Loomo started watching this page.");
        var lines = new List<string> { $"Page: {url}", "Recent errors and warnings (newest first):" };
        foreach (var entry in entries) {
            var where = entry.Line > 0 ? $"{entry.Url}:{entry.Line}" : entry.Url;
            lines.Add($"- [{entry.Kind}] {entry.Text}" + (where.Length > 0 ? $"  ({where})" : ""));
        }
        return new LoomoRoomResult(true, string.Join("\n", lines));
    }
}
