using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Azure DevOps の PR のページで、コメントを右クリック →「手元で開く」（§42.3）。
/// <list type="number">
/// <item>右クリックした要素から外側へ、各段の<b>表示文字だけ</b>を読む（自前のスクリプトの戻り値。ページからの合図は受けない §41.2）。</item>
/// <item>PR のスレッド（REST・GET のみ）と照らし合わせてスレッドを決める（<see cref="ReviewThreadLocator"/>）。答えのファイルと行は REST の側から。</item>
/// <item>開く場所は PR の元のブランチをチェックアウトしているワークツリー → <c>pr/&lt;番号&gt;</c> の順（<see cref="ReviewWorktreeChooser"/>）。
/// どちらも無ければ黙って本体を開かず、「本体で開く（ブランチ違い）」として出す。</item>
/// <item>行は PR の版のその行の中身で手元に合わせる（<see cref="ReviewLineAnchor"/>）。</item>
/// </list>
/// 項目名に行き先（ワークツリー・ファイル:行）を書くので、押す前にどこへ飛ぶか分かる。当たらなければ項目を出さない。
/// メニューは deferral で待たせるので、問い合わせは短く打ち切る（PR の中身は少しの間覚えておく）。
/// </summary>
public partial class ShellWindow {
    private readonly Dictionary<string, (DateTime At, Task<AzureDevOpsPullRequestDetail?> Task)> _reviewDetails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ReviewDetailTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReviewMenuBudget = TimeSpan.FromSeconds(3);

    /// <summary>右クリックした所から外側へ、各段の表示文字（内側が先・長すぎる段で止める）。座標はページの CSS 座標。</summary>
    private const string ReviewAncestorTextsScript = """
(function(x,y){
  var el=document.elementFromPoint(x,y), out=[];
  for(var i=0; el && i<12; i++, el=el.parentElement){
    var t=(el.innerText||'').trim();
    if(t.length>4000) break;
    if(t.length>0 && (out.length===0 || out[out.length-1]!==t)) out.push(t);
  }
  return JSON.stringify(out);
})(%X%,%Y%)
""";

    /// <summary>PR のページなら、右クリックのメニューに「手元で開く」を足す（ほかのページでは何もしない）。</summary>
    private void AddReviewThreadMenuItems(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e) {
        if (AzureDevOpsWebUrl.TryParse(core.Source) is not { Kind: AzureDevOpsWebPageKind.PullRequest } page)
            return;
        var deferral = e.GetDeferral();
        _ = FillReviewThreadMenuAsync(tab, core, e, page, deferral);
    }

    private async Task FillReviewThreadMenuAsync(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e,
        AzureDevOpsWebPage page, CoreWebView2Deferral deferral) {
        try {
            var work = BuildReviewThreadItemsAsync(tab, core, e, page);
            if (await Task.WhenAny(work, Task.Delay(ReviewMenuBudget)) != work)
                return;   // 間に合わなければ出さない（メニューを待たせ続けない）
            var items = await work;
            for (var i = items.Count - 1; i >= 0; i--)
                e.MenuItems.Insert(0, items[i]);
            if (items.Count > 0 && e.MenuItems.Count > items.Count)
                e.MenuItems.Insert(items.Count, core.Environment.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException) { }
        finally {
            deferral.Complete();
        }
    }

    private async Task<List<CoreWebView2ContextMenuItem>> BuildReviewThreadItemsAsync(
        BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e, AzureDevOpsWebPage page) {
        var items = new List<CoreWebView2ContextMenuItem>();
        // WebView2 の座標は DIP、elementFromPoint は CSS px（右クリックされたタブの拡大率で割る。前面のタブとは限らない）。
        var zoom = tab.View.ZoomFactor is > 0 and var z ? z : 1.0;
        var script = ReviewAncestorTextsScript
            .Replace("%X%", ((int)(e.Location.X / zoom)).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("%Y%", ((int)(e.Location.Y / zoom)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var textsTask = core.ExecuteScriptAsync(script);
        var detail = await ReviewDetailAsync(page);
        var raw = await textsTask;
        if (detail is null || JsonSerializer.Deserialize<string>(raw) is not { } json)
            return items;
        var texts = JsonSerializer.Deserialize<List<string>>(json) ?? [];
        var discussionId = DiscussionIdOf(core.Source);
        var thread = ReviewThreadLocator.Find(texts, detail.Threads.Where(t => t.FilePath is not null && t.Line is > 0).ToList(), discussionId);
        if (thread is null)
            return items;

        var clone = await FindRepositoryByRemoteAsync(url => AzureDevOpsPullRequestJson.RemoteMatches(url, page.Project, page.Repository));
        if (clone is null)
            return items;   // 手元にクローンが無い：開く先が無い
        var worktrees = await _git.For(clone.Value.Root).GetWorktreesAsync();
        var relative = thread.FilePath!.TrimStart('/');
        var fileName = Path.GetFileName(relative);
        if (ReviewWorktreeChooser.Choose(worktrees, detail.Summary.SourceBranch, detail.Summary.Id) is { } worktree) {
            var label = $"手元で開く — {Path.GetFileName(worktree.Path.TrimEnd('\\'))}（{worktree.Branch}）の {fileName}:{thread.Line}";
            items.Add(MenuCommand(core, label, () => _ = OpenReviewThreadLocallyAsync(worktree.Path, relative, thread.Line!.Value, detail.SourceCommit)));
        }
        else {
            var main = worktrees.FirstOrDefault(w => w.IsMain)?.Path ?? clone.Value.Root;
            var branch = worktrees.FirstOrDefault(w => w.IsMain)?.Branch ?? "?";
            items.Add(MenuCommand(core, $"手元の本体で開く — {fileName}:{thread.Line}（ブランチは {branch}。PR の {detail.Summary.SourceBranch} ではない）",
                () => _ = OpenReviewThreadLocallyAsync(main, relative, thread.Line!.Value, detail.SourceCommit)));
        }
        return items;
    }

    private CoreWebView2ContextMenuItem MenuCommand(CoreWebView2 core, string label, Action action) {
        var item = core.Environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(action);
        return item;
    }

    /// <summary>ワークツリーのそのファイルを、PR の版の行の中身で合わせた行で開く。</summary>
    private async Task OpenReviewThreadLocallyAsync(string worktree, string relative, int line, string sourceCommit) {
        var path = Path.GetFullPath(Path.Combine(worktree, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) {
            ToastService.Error($"手元に {relative} がありません（{worktree}）。");
            return;
        }
        IReadOnlyList<string>? prLines = null;
        if (sourceCommit.Length > 0) {
            var shown = await _git.For(worktree).RunAsync("show", $"{sourceCommit}:{relative}");
            if (shown.Success)
                prLines = shown.Output.Replace("\r\n", "\n").Split('\n');
        }
        var localLines = await File.ReadAllLinesAsync(path);
        var match = ReviewLineAnchor.Map(line, prLines, localLines);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await OpenPathInEditorAsync(path, match.Line, 0);
        if (!match.Exact && prLines is not null)
            ToastService.Info("PR の版のその行が手元に見つからなかったので、行番号のまま開きました（手元の版と違うかもしれません）。");
    }

    /// <summary>PR の中身（スレッド込み）。右クリックのたびに問い合わせない（少しの間覚えておく）。
    /// <para><paramref name="refresh"/> なら覚えを使わず読み直す（ブラウザサポートの「未解決の指摘」：ページで解決した
    /// スレッドを残して見せない）。読み直したものは右クリックの覚えにもなる。</para></summary>
    private Task<AzureDevOpsPullRequestDetail?> ReviewDetailAsync(AzureDevOpsWebPage page, bool refresh = false) {
        var key = $"{page.OrganizationUrl}|{page.Project}|{page.Repository}|{page.PullRequestId}";
        if (!refresh && _reviewDetails.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ReviewDetailTtl)
            return cached.Task;
        var task = LoadAsync();
        _reviewDetails[key] = (DateTime.UtcNow, task);
        return task;

        async Task<AzureDevOpsPullRequestDetail?> LoadAsync() {
            if (await ConnectAzureForCommentsAsync(CancellationToken.None) is not { } connection)
                return null;
            try {
                return await connection.Client.GetPullRequestDetailAsync(page.OrganizationUrl,
                    new AzureDevOpsPrTarget(page.Project, page.Repository), page.PullRequestId, connection.Auth, CancellationToken.None);
            }
            catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException) {
                _reviewDetails.Remove(key);
                return null;
            }
        }
    }

    private static int DiscussionIdOf(string? url)
        => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && System.Web.HttpUtility.ParseQueryString(uri.Query)["discussionId"] is { } value && int.TryParse(value, out var id) ? id : 0;

    /// <summary>PR のページを開いたら、スレッドを先に読んでおく（右クリックのメニューを待たせない）。</summary>
    private void PrefetchReviewThreads(string? url) {
        if (AzureDevOpsWebUrl.TryParse(url) is { Kind: AzureDevOpsWebPageKind.PullRequest } page)
            _ = ReviewDetailAsync(page);
    }
}
