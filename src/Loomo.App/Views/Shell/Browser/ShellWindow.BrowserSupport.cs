using System.Windows.Threading;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザサポートペイン（§41）。前に出ているブラウザのタブのページを追従元にして、そのサイトに合った
/// 道具（<see cref="BrowserSupportCatalog"/>）を出す。道具は<b>ページを部屋の中へつなぐ</b>もの——Azure DevOps の PR を
/// Diff ペインで、ファイルを手元のクローンのその行で。ページの中身（DOM）は読まず、URL だけから決める。
/// <para>引き直しは合図をまとめて 1 回にし、<b>ペインが見えていないときは何もしない</b>（見えた瞬間に追いつく。§39.2 と同じ）。</para>
/// </summary>
public partial class ShellWindow {
    private readonly BrowserSupportViewModel _browserSupport = new();
    private bool _browserSupportRefreshQueued;
    /// <summary>いま見せているページの手元のクローンのリモート名（コミットを取ってくるとき）。</summary>
    private string? _browserSupportRemote;

    private void InitializeBrowserSupport() {
        BrowserSupportPane.DataContext = _browserSupport;
        BrowserSupportPaneHost.DataContext = _browserSupport;
        _browserSupport.ActionRequested += action => _ = RunBrowserSupportActionAsync(action);
        _vm.Browser.CurrentPageChanged += (_, _) => RequestBrowserSupportRefresh();
        // フォルダーの出し入れでクローンの有無が変わる：同じページでも探し直す。
        _workspace.FoldersChanged += (_, _) => Dispatcher.BeginInvoke(() => RequestBrowserSupportRefresh(force: true));
        BrowserSupportPane.IsVisibleChanged += (_, e) => {
            if (e.NewValue is true)
                RequestBrowserSupportRefresh();
        };
    }

    private void RequestBrowserSupportRefresh() => RequestBrowserSupportRefresh(force: false);

    private void RequestBrowserSupportRefresh(bool force) {
        if (force)
            _browserSupport.Forget();
        if (_browserSupportRefreshQueued || !BrowserSupportPane.IsVisible)
            return;
        _browserSupportRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshBrowserSupport);
    }

    private void RefreshBrowserSupport() {
        _browserSupportRefreshQueued = false;
        var url = _vm.Browser.CurrentUrl;
        var page = BrowserSupportCatalog.Describe(url);
        if (!_browserSupport.Show(page, url))
            return;
        _browserSupportRemote = null;
        if (page?.AzureDevOps is { } ado)
            _ = FindBrowserSupportCloneAsync(page, ado);
    }

    private async Task FindBrowserSupportCloneAsync(BrowserSupportPage page, AzureDevOpsWebPage ado) {
        (string Root, string Remote)? clone;
        try {
            clone = await FindAzureRepositoryAsync(ado.Project, ado.Repository);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            _browserSupport.SetRepository(page, null, $"手元のクローンを探せませんでした: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(_browserSupport.Page, page))
            return;
        _browserSupportRemote = clone?.Remote;
        _browserSupport.SetRepository(page, clone?.Root, clone is { } found
            ? $"手元のクローン: {_workspace.ToDisplayPath(found.Root)}（{found.Remote}）"
            : $"ワークスペースに {ado.Project} / {ado.Repository} のクローンがありません。差分・ファイルを開くには、そのリポジトリのフォルダーを開いてください。");
    }

    private async Task RunBrowserSupportActionAsync(BrowserSupportAction action) {
        if (_browserSupport.Page?.AzureDevOps is not { } ado)
            return;
        var root = _browserSupport.RepositoryRoot;
        switch (action.Kind) {
            case BrowserSupportActionKind.PullRequestDiff when root is not null:
                // クローン探しはパレットからの差分と同じ（いまの対象→全リポジトリ。見つかればそれを Git の対象にして取る）。
                if (await ReadBrowserSupportPullRequestAsync(ado) is { } diffDetail)
                    await OpenAzurePullRequestDiffAsync(diffDetail.Summary);
                break;
            case BrowserSupportActionKind.PullRequestDocument:
                if (await ReadBrowserSupportPullRequestAsync(ado) is { } detail)
                    await ShowAzurePullRequestDocumentAsync(detail);
                break;
            case BrowserSupportActionKind.OpenLocalFile when root is not null:
                await OpenBrowserSupportFileAsync(ado, root);
                break;
            case BrowserSupportActionKind.CommitDiff when root is not null:
                await OpenBrowserSupportCommitAsync(ado, root);
                break;
            case BrowserSupportActionKind.SelectRepository when root is not null:
                SelectGitRepository(root);
                _browserSupport.ReportStatus($"Git の対象を {_workspace.ToDisplayPath(root)} にしました。");
                break;
        }
    }

    /// <summary>ページの PR を読む（読むだけ。§24.27.1 と同じ接続）。照会先の組織はページの URL のもの——
    /// 設定の組織とは別の組織の PR を見ていることもある。</summary>
    private async Task<AzureDevOpsPullRequestDetail?> ReadBrowserSupportPullRequestAsync(AzureDevOpsWebPage ado) {
        if (_vm.WorkItems is not { } workItems)
            return null;
        try {
            var connection = await workItems.ConnectAsync(CancellationToken.None);
            return await workItems.Client.GetPullRequestDetailAsync(ado.OrganizationUrl,
                new AzureDevOpsPrTarget(ado.Project, ado.Repository), ado.PullRequestId, connection.Auth, CancellationToken.None);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException) {
            _browserSupport.ReportStatus($"PR {ado.PullRequestId} を読めませんでした: {ex.Message}");
            return null;
        }
    }

    private async Task OpenBrowserSupportFileAsync(AzureDevOpsWebPage ado, string root) {
        if (AzureDevOpsWebUrl.LocalPath(root, ado.FilePath) is not { } path) {
            _browserSupport.ReportStatus($"{ado.FilePath} は手元のクローンの中を指していません。");
            return;
        }
        if (!File.Exists(path)) {
            _browserSupport.ReportStatus(ado.VersionBranch is { } branch
                ? $"手元に {ado.FilePath} がありません（ページはブランチ {branch} のもの。手元のブランチには無いのかもしれません）。"
                : $"手元に {ado.FilePath} がありません。");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await OpenPathInEditorAsync(path, ado.Line, 0);
    }

    /// <summary>ページのコミットを Diff ペインで。手元に無ければ、そのリポジトリを指すリモートから取ってくる。</summary>
    private async Task OpenBrowserSupportCommitAsync(AzureDevOpsWebPage ado, string root) {
        var git = _git.For(root);
        var hash = await ResolveCommitAsync(git, ado.CommitHash);
        if (hash is null && _browserSupportRemote is { } remote) {
            _browserSupport.ReportStatus($"コミット {ado.CommitHash[..Math.Min(7, ado.CommitHash.Length)]} を {remote} から取ってきています…");
            var fetch = await git.RunAsync("fetch", "--no-tags", remote);
            if (!fetch.Success) {
                _browserSupport.ReportStatus($"{remote} から取ってこられませんでした: {fetch.Message}");
                return;
            }
            hash = await ResolveCommitAsync(git, ado.CommitHash);
        }
        if (hash is null) {
            _browserSupport.ReportStatus($"コミット {ado.CommitHash} が手元にもリモートにも見つかりません。");
            return;
        }
        SelectGitRepository(root);
        ShowDiff(new DiffOpenTarget.CommitRange(null, hash, $"コミット {hash[..7]}"));
        _browserSupport.ReportStatus("");
    }

    private static async Task<string?> ResolveCommitAsync(GitService git, string hash) {
        var result = await git.RunAsync("rev-parse", "--verify", "--quiet", hash + "^{commit}");
        return result.Success && result.Output.Trim() is { Length: >= 7 } full ? full : null;
    }
}
