namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: PR のレビューのコメントをエディタのその行に出す配線（§42）。中身は <see cref="PullRequestCommentController"/>。
/// 配線はテストの印と同じ2か所——エディタを作ったとき（<c>BuildEditorControl</c>）と、ファイルを読み込んだとき
/// （<c>AfterEditorFileLoaded</c>：<c>LoadFile</c> は印を捨てるのに <c>BufferChanged</c> を出さないので）。
/// </summary>
public partial class ShellWindow {
    private PullRequestCommentController _pullRequestComments = null!;

    private void InitializePullRequestComments() {
        _pullRequestComments = new PullRequestCommentController(_git, ConnectAzureForCommentsAsync, Dispatcher,
            url => _ = ShowBrowserPaneAndNavigateAsync(url));
        // PR のページを開いたら、そのスレッドを先に読んでおく（右クリックの「手元で開く」を待たせない §42.3）。
        _vm.Browser.CurrentPageChanged += (_, _) => PrefetchReviewThreads(_vm.Browser.CurrentUrl);
        // Claude への指摘（§45.4 P1）の印も、同じエディタの印に出す。
        _pullRequestComments.ExtraMarkers = control => ReviewMarksFor(control.FilePath);
        _pullRequestComments.ExtraTooltip = (control, line0) => ReviewTooltipFor(control.FilePath, line0);
        _pullRequestComments.ExtraMarkerClicked = (_, _) => OpenReviewNotes();
    }

    /// <summary>Work Items と同じ接続（設定・PAT の候補）。設定が無い・繋がらないなら null（印を出さないだけ）。</summary>
    private async Task<(AzureDevOpsWorkItemClient Client, System.Net.Http.Headers.AuthenticationHeaderValue Auth)?> ConnectAzureForCommentsAsync(
        CancellationToken cancellationToken) {
        if (_vm.WorkItems is not { } workItems)
            return null;
        try {
            var connection = await workItems.ConnectAsync(cancellationToken);
            return (workItems.Client, connection.Auth);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or InvalidOperationException or System.Net.Http.HttpRequestException) {
            return null;
        }
    }
}
