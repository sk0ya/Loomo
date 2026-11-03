using System.Windows.Threading;
using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザサポートペイン（§41）。前に出ているブラウザのタブのページを追従元にして、そのサイトに合った
/// 道具（<see cref="BrowserSupportCatalog"/>）と、ページから読み出した行の節を出す。道具は<b>ページを部屋の中へつなぐ</b>もの——
/// PR を Diff ペインで、ファイルを手元のクローンのその行で、CI のエラーを手元のファイルで、記事のコードをエディタ・ターミナルへ。
/// <para>引き直しは合図をまとめて 1 回にし、<b>ペインが見えていないときは何もしない</b>（見えた瞬間に追いつく。§39.2 と同じ）。</para>
/// <para>サイトごとの中身は ShellWindow.BrowserSupportSites.cs（Azure DevOps・GitHub・CI・パッケージ）。</para>
/// </summary>
public partial class ShellWindow {
    private readonly BrowserSupportViewModel _browserSupport = new();
    private bool _browserSupportRefreshQueued;
    /// <summary>いま見せているページの手元のクローンのリモート名（コミット・PR を取ってくるとき）。</summary>
    private string? _browserSupportRemote;
    /// <summary>ページの読み込みが終わった合図のあとの引き直しか（同じページでもコードを読み直す）。</summary>
    private bool _browserSupportLoaded;

    private const int SectionProblems = 10, SectionFailedSteps = 20, SectionUsages = 30, SectionOtherProjects = 40,
        SectionPort = 50, SectionCode = 60, SectionClips = 70;

    private void InitializeBrowserSupport() {
        BrowserSupportPane.DataContext = _browserSupport;
        BrowserSupportPaneHost.DataContext = _browserSupport;
        _browserSupport.ActionRequested += action => _ = RunBrowserSupportActionAsync(action);
        _vm.Browser.CurrentPageChanged += (_, _) => RequestBrowserSupportRefresh();
        // ページの読み込みが終わってからでないとコードブロックが揃わない（遷移の合図はまだ前のページの DOM のうちに来る）。
        // 題もこの頃に確定する（普通のページの見出し）。
        _vm.Browser.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(BrowserViewModel.IsLoading) && !_vm.Browser.IsLoading) {
                _browserSupportLoaded = true;
                RequestBrowserSupportRefresh();
            }
        };
        // ペグボードに取った・消した抜き書きを、いま見ているページの節へ。
        _vm.Pegboard.Changed += (_, _) => {
            if (BrowserSupportPane.IsVisible && _browserSupport.Page is { } page)
                ShowBrowserSupportClips(page);
        };
        // フォルダーの出し入れでクローンの有無・パッケージの使われ方が変わる：同じページでも作り直す。
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
        if (page is { IsGeneric: true, Heading.Length: 0 } generic && _vm.Browser.CurrentTitle is { Length: > 0 } title)
            page = generic with { Heading = title, Detail = generic.Site, Site = "このページ" };
        var loaded = _browserSupportLoaded;
        _browserSupportLoaded = false;
        if (!_browserSupport.Show(page, url)) {
            if (loaded && _browserSupport.Page is { ShowsCode: true } same)
                _ = LoadBrowserSupportCodeAsync(same);
            return;
        }
        if (page is null)
            return;
        _browserSupportRemote = null;
        _browserSupportLog = null;
        if (page.HasRepository)
            _ = FindBrowserSupportCloneAsync(page);
        else if (page.AzureDevOps is { Kind: AzureDevOpsWebPageKind.Build })
            _ = LoadAzureBuildFailuresAsync(page);
        if (page.Package is { } package)
            _ = LoadPackageUsagesAsync(page, package);
        if (page.LocalPort > 0)
            _ = LoadLocalPortOwnersAsync(page);
        if (page.ShowsCode && !_vm.Browser.IsLoading)
            _ = LoadBrowserSupportCodeAsync(page);
        ShowBrowserSupportClips(page);
    }

    /// <summary>ページのリポジトリの手元のクローンを探す（§41.3.1）。見つかったあとに要る節（Actions の失敗）もここから読む。</summary>
    private async Task FindBrowserSupportCloneAsync(BrowserSupportPage page) {
        var (label, matches) = page.GitHub is { } github
            ? (github.Slug, (Func<string, bool>)(url => GitHostingUrl.TryGetGitHubRepositoryUrl(url, out var repo)
                && string.Equals(repo, github.RepositoryUrl, StringComparison.OrdinalIgnoreCase)))
            : ($"{page.AzureDevOps!.Project} / {page.AzureDevOps.Repository}",
                url => AzureDevOpsPullRequestJson.RemoteMatches(url, page.AzureDevOps.Project, page.AzureDevOps.Repository));
        (string Root, string Remote)? clone;
        try {
            clone = await FindRepositoryByRemoteAsync(matches);
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
            : $"ワークスペースに {label} のクローンがありません。差分・ファイルを開くには、そのリポジトリのフォルダーを開いてください。");
        if (page.GitHub is { Kind: GitHubWebPageKind.ActionsRun })
            _ = LoadGitHubRunFailuresAsync(page);
    }

    private async Task RunBrowserSupportActionAsync(BrowserSupportAction action) {
        if (_browserSupport.Page is not { } page)
            return;
        var root = _browserSupport.RepositoryRoot;
        switch (action.Kind) {
            case BrowserSupportActionKind.SelectRepository when root is not null:
                SelectGitRepository(root);
                _browserSupport.ReportStatus($"Git の対象を {_workspace.ToDisplayPath(root)} にしました。");
                return;
            case BrowserSupportActionKind.OpenSourceFile when page.LocalFilePath is { } path:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
                await OpenPathInEditorAsync(path, 0, 0);
                return;
        }
        if (page.AzureDevOps is { } ado)
            await RunAzureDevOpsSupportAsync(action.Kind, page, ado, root);
        else if (page.GitHub is { } github)
            await RunGitHubSupportAsync(action.Kind, page, github, root);
    }

    // ── どのページでも：抜き書き ───────────────────────────────────────

    /// <summary>このページからペグボードへ取った抜き書き（§24.24 の出典付き）。押すとこのタブのまま、その一節へ
    /// Text Fragment で戻る。無ければ節ごと出さない。</summary>
    private void ShowBrowserSupportClips(BrowserSupportPage page) {
        var url = _vm.Browser.CurrentUrl;
        var clips = _vm.Pegboard.Items
            .Where(item => item.HasWebSource && BrowserLibrary.SameUrl(item.Snapshot.SourceUrl, url))
            .ToList();
        if (clips.Count == 0) {
            _browserSupport.RemoveSection(page, "clips");
            return;
        }
        var rows = clips.Select(item => new BrowserSupportRow(BrowserPageCode.Preview(item.Content, 4), "", [
            new BrowserSupportButton("この箇所へ", () => {
                if (item.SourceLink is { } link)
                    ActiveBrowserView.TryCore()?.Navigate(link);
            }, "このタブのまま、取った一節までスクロールして強調する"),
            new BrowserSupportButton("コンポーザへ", () => InsertIntoComposer(item.Content)),
        ])).ToList();
        _browserSupport.SetSection(page, "clips", $"このページの抜き書き（{rows.Count}）", SectionClips, rows);
    }

    // ── 記事・ドキュメント：ページのコード ─────────────────────────────

    /// <summary>ページのコードブロックを読み、エディタ・コンポーザ・ターミナル・ペグボードへ送れる行にする。
    /// 読むのはページの中で走らせる自前のスクリプトの<b>戻り値</b>だけ（ページからの合図は受けない）。</summary>
    private async Task LoadBrowserSupportCodeAsync(BrowserSupportPage page) {
        if (ActiveBrowserView.TryCore() is not { } core)
            return;
        var url = _vm.Browser.CurrentUrl;
        var title = _vm.Browser.CurrentTitle;
        string? result;
        try {
            result = await core.ExecuteScriptAsync(BrowserPageCode.ExtractScript);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
            return;   // タブが閉じた・作り直された
        }
        if (!ReferenceEquals(_browserSupport.Page, page) || _vm.Browser.CurrentUrl != url)
            return;
        var blocks = BrowserPageCode.Parse(result);
        if (blocks.Count == 0) {
            _browserSupport.RemoveSection(page, "code");
            return;
        }
        var rows = blocks.Select(block => {
            var buttons = new List<BrowserSupportButton> {
                new("エディタへ", () => _ = OpenCodeBlockInEditorAsync(block), "新しいタブに写す（保存先は自分で決める）"),
                new("コンポーザへ", () => InsertIntoComposer(block.Text)),
            };
            if (block.LooksLikeCommand)
                buttons.Add(new("ターミナルへ", () => SendRecentCommandToTerminal(BrowserPageCode.AsCommand(block.Text)),
                    "見えるターミナルのプロンプトへ入れる（Enter は押さない。複数行はコンポーザへ）"));
            buttons.Add(new("ペグボードへ", () => _vm.Pegboard.AddContent(block.Text, type: "text", sourceUrl: url, sourceTitle: title),
                "出典付きで残す（§24.24）"));
            var language = block.Language.Length > 0 ? block.Language : "言語の指定なし";
            return new BrowserSupportRow(BrowserPageCode.Preview(block.Text), $"{language} · {block.LineCount} 行", buttons, Mono: true);
        }).ToList();
        var section = _browserSupport.SetSection(page, "code", $"このページのコード（{rows.Count}）", SectionCode, rows);
        if (section is { Buttons.Count: 0 })
            section.Buttons.Add(new BrowserSupportButton("読み直す", () => _ = LoadBrowserSupportCodeAsync(page), "ページが後から書き足したコードも拾う"));
    }

    private async Task OpenCodeBlockInEditorAsync(BrowserCodeBlock block) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            FileName = "snippet" + BrowserPageCode.ExtensionFor(block.Language),
            Content = block.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            OnSaved = _ => { },   // ページからの写し：どこにも書き戻さない
        });
    }

    // ── localhost：このポートを動かしているターミナル ──────────────────

    /// <summary>ページのポートで待ち受けているプロセスを引き、それがどのターミナルのタブの子孫かを見る
    /// （§39.4.2 の Claude のセッション探しと同じ「シェルの PID の子孫」）。</summary>
    private async Task LoadLocalPortOwnersAsync(BrowserSupportPage page) {
        var shells = _terminalTabs
            .Select(tab => (tab.Id, Shell: TerminalShellProcessId(tab.Id), Title: TerminalTabTitle(tab.Id) ?? "Terminal"))
            .Where(t => t.Shell is > 0)
            .ToList();
        _browserSupport.SetSection(page, "port", $"ポート {page.LocalPort} を開いているもの", SectionPort, [], "調べています…");
        List<(int Pid, string Name, (Guid Id, int? Shell, string Title)? Tab)> owners;
        try {
            owners = await Task.Run(() => {
                var processes = sk0ya.Loomo.Services.ClaudeCode.ClaudeLiveSessions.SnapshotParents();
                return ListeningPorts.OwnersOf(page.LocalPort).Select(pid => {
                    var name = processes.TryGetValue(pid, out var info) ? info.Name : "?";
                    var tab = shells.FirstOrDefault(t => t.Shell == pid
                        || sk0ya.Loomo.Services.ClaudeCode.ClaudeLiveSessions.IsDescendantOf(pid, t.Shell!.Value, processes));
                    return (pid, name, tab.Shell is null ? ((Guid, int?, string)?)null : tab);
                }).ToList();
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            _browserSupport.SetSection(page, "port", $"ポート {page.LocalPort} を開いているもの", SectionPort, [], $"調べられませんでした: {ex.Message}");
            return;
        }
        var rows = owners.Select(owner => new BrowserSupportRow(
            $"{owner.Name}（PID {owner.Pid}）",
            owner.Tab is { } tab ? $"ターミナル「{tab.Title}」で動いています" : "Loomo のターミナルの外で動いています",
            owner.Tab is { } target ? [new BrowserSupportButton("このタブへ", () => GoToTerminalTab(target.Id))] : [])).ToList();
        _browserSupport.SetSection(page, "port", $"ポート {page.LocalPort} を開いているもの", SectionPort, rows,
            rows.Count == 0 ? "このポートで待ち受けているプロセスはありません（サーバーが止まっている）。" : "");
    }
}
