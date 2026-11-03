using System.Windows.Threading;
using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザサポートペイン（§41）。前に出ているブラウザのタブのページを追従元にして、そのサイトに合った
/// 道具（<see cref="BrowserSupportCatalog"/>）と、ページから読み出した行の節を出す。道具は<b>ページを部屋の中へつなぐ</b>もの——
/// PR の指摘を手元の行で、ファイルを手元のクローンのその行で、CI のエラーを手元のファイルで、PR・Issue を Claude Code へ。
/// 記事のコードはペインに並べ直さず、ページのコードブロックの右クリックから送る（ShellWindow.BrowserPageCodeMenu.cs）。
/// <para>引き直しは合図をまとめて 1 回にし、<b>ペインが見えていないときは何もしない</b>（見えた瞬間に追いつく。§39.2 と同じ）。</para>
/// <para>サイトごとの中身は ShellWindow.BrowserSupportSites.cs（Azure DevOps・GitHub・CI・パッケージ）。</para>
/// </summary>
public partial class ShellWindow {
    private readonly BrowserSupportViewModel _browserSupport = new();
    private bool _browserSupportRefreshQueued;
    /// <summary>いま見せているページの手元のクローンのリモート名（コミット・PR を取ってくるとき）。</summary>
    private string? _browserSupportRemote;

    private const int SectionOrigin = 0, SectionReview = 5, SectionProblems = 10, SectionUsages = 30, SectionOtherProjects = 40,
        SectionPort = 50;

    private void InitializeBrowserSupport() {
        BrowserSupportPane.DataContext = _browserSupport;
        BrowserSupportPaneHost.DataContext = _browserSupport;
        _browserSupport.ActionRequested += action => _ = RunBrowserSupportActionAsync(action);
        _vm.Browser.CurrentPageChanged += (_, _) => RequestBrowserSupportRefresh();
        // 題は読み込みが終わる頃に確定する（普通のページの見出し）。
        _vm.Browser.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(BrowserViewModel.IsLoading) && !_vm.Browser.IsLoading)
                RequestBrowserSupportRefresh();
        };
        // フォルダーの出し入れでクローンの有無・パッケージの使われ方が変わる：同じページでも作り直す。
        _workspace.FoldersChanged += (_, _) => Dispatcher.BeginInvoke(() => RequestBrowserSupportRefresh(force: true));
        BrowserSupportPane.IsVisibleChanged += (_, e) => {
            if (e.NewValue is true)
                RequestBrowserSupportRefresh();
        };
    }

    private void RequestBrowserSupportRefresh() => RequestBrowserSupportRefresh(force: false);

    /// <summary>Claude Code が動いているタブがあるか（「Claude Code へ渡す」を出すか）。ページの引き直しのほか、タブのタイトルの印が
    /// 変わったとき（起きた・終わった）とタブを閉じたときにも呼ぶ——ページを移るまで古いままにしない。</summary>
    private void UpdateBrowserSupportClaude() => _browserSupport.SetClaudeAvailable(ClaudeTerminalTab() is not null);

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
        UpdateBrowserSupportClaude();
        if (!_browserSupport.Show(page, url)) {
            // 同じページでも別のタブかもしれない（調べ始めた場所はタブのもの）。
            if (_browserSupport.Page is { } current)
                ShowBrowserSupportOrigin(current);
            return;
        }
        if (page is null)
            return;
        ShowBrowserSupportOrigin(page);
        _browserSupportRemote = null;
        if (page.HasRepository)
            _ = FindBrowserSupportCloneAsync(page);
        else if (page.AzureDevOps is { Kind: AzureDevOpsWebPageKind.Build })
            _ = LoadAzureBuildFailuresAsync(page);
        if (page.Package is { } package)
            _ = LoadPackageUsagesAsync(page, package);
        if (page.LocalPort > 0)
            _ = LoadLocalPortOwnersAsync(page);
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
            ? $"手元のクローン: {CloneDisplayName(found.Root)} · リモート {found.Remote}"
            : $"ワークスペースに {label} のクローンがありません。差分・ファイルを開くには、そのリポジトリのフォルダーを開いてください。");
        if (page.GitHub is { Kind: GitHubWebPageKind.ActionsRun })
            _ = LoadGitHubRunFailuresAsync(page);
        if (page.AzureDevOps is { Kind: AzureDevOpsWebPageKind.PullRequest } && clone is { } local)
            _ = LoadPullRequestReviewAsync(page, local.Root, local.Remote);
    }

    /// <summary>クローンの呼び名。ワークスペースのフォルダーそのものだと表示パスは空になる（フォルダーが1つのとき）ので、フォルダー名にする。</summary>
    private string CloneDisplayName(string root) {
        var display = _workspace.ToDisplayPath(root);
        return display.Length > 0 ? display : Path.GetFileName(root.TrimEnd('\\', '/'));
    }

    private async Task RunBrowserSupportActionAsync(BrowserSupportAction action) {
        if (_browserSupport.Page is not { } page)
            return;
        var root = _browserSupport.RepositoryRoot;
        switch (action.Kind) {
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

    private async Task OpenCodeBlockInEditorAsync(BrowserCodeBlock block) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            FileName = "snippet" + BrowserPageCode.ExtensionFor(block.Language),
            Content = block.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            OnSaved = _ => { },   // ページからの写し：どこにも書き戻さない
        });
    }

    // ── どのページでも：調べ始めた場所（§41.12） ───────────────────────

    /// <summary>前面のブラウザのタブの調べ始めた場所。ファイルが消えた・ターミナルのタブを閉じたなら無いものとする。</summary>
    private BrowserSearchOrigin? CurrentSearchOrigin => SearchOriginOf(_activeBrowserTab);

    /// <summary>そのタブの調べ始めた場所（右クリックされたタブは前面のタブとは限らない）。</summary>
    private BrowserSearchOrigin? SearchOriginOf(BrowserTab? tab) => tab?.SearchOrigin switch {
        BrowserSearchOrigin.Editor e when File.Exists(e.FilePath) => e,
        BrowserSearchOrigin.Terminal t when _terminalTabs.Any(tab => tab.Id == t.TabId) => t,
        _ => null,
    };

    /// <summary>節「調べ始めた場所」：右クリック「ブラウザへ送る」で来たタブなら、その場所と「戻る」。
    /// タブの中でリンクを辿っても・子のタブへ移っても出続ける。</summary>
    private void ShowBrowserSupportOrigin(BrowserSupportPage page) {
        if (CurrentSearchOrigin is not { } origin) {
            _browserSupport.RemoveSection(page, "origin");
            return;
        }
        // 見出しはファイル名:行だけ（ワークスペースの外のファイルだと表示パスが何行にも折り返す）。場所は押す前にツールチップで。
        var (title, tip) = origin switch {
            BrowserSearchOrigin.Editor e => ($"{Path.GetFileName(e.FilePath)}:{e.Line + 1}", $"エディタのその行へ戻る（{_workspace.ToDisplayPath(e.FilePath)}）"),
            BrowserSearchOrigin.Terminal t => ($"ターミナル「{TerminalTabTitle(t.TabId) ?? "Terminal"}」", "そのターミナルのタブへ戻る"),
            _ => ("", ""),
        };
        var row = new BrowserSupportRow(title, $"「{origin.QueryPreview}」を調べています",
            [new BrowserSupportButton("戻る", () => _ = ReturnToSearchOriginAsync(origin), tip)],
            origin is BrowserSearchOrigin.Editor ? BrowserSupportRowLook.Path : BrowserSupportRowLook.Text);
        _browserSupport.SetSection(page, "origin", "調べ始めた場所", SectionOrigin, [row], emphasis: true);
    }

    private async Task ReturnToSearchOriginAsync(BrowserSearchOrigin origin) {
        switch (origin) {
            case BrowserSearchOrigin.Editor e:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
                await OpenPathInEditorAsync(e.FilePath, e.Line + 1, e.Column + 1);
                FocusPane(PaneKind.Editor);
                break;
            case BrowserSearchOrigin.Terminal t:
                GoToTerminalTab(t.TabId);
                break;
        }
    }

    /// <summary>ページのコードを、調べ始めた所の下の行へ差し込む（1 回の取り消せる編集）。続けて貼ったものは前に貼ったものの
    /// 下へ並ぶよう、タブの出所の「最後の行」を貼った末尾へ進める（戻る先の行は変えない）。</summary>
    private async Task PasteCodeAtSearchOriginAsync(BrowserTab browserTab, BrowserCodeBlock block) {
        if (SearchOriginOf(browserTab) is not BrowserSearchOrigin.Editor origin) {
            ToastService.Info("調べ始めたファイルが見つかりません");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await OpenPathInEditorAsync(origin.FilePath, 0, 0);
        if (_activeEditorTab is not { IsRealized: true } tab
            || !string.Equals(tab.PeekFilePath, origin.FilePath, StringComparison.OrdinalIgnoreCase)) {
            ToastService.Info("調べ始めたファイルを開けませんでした");
            return;
        }
        var paste = BrowserSearchOrigin.PasteBelow(tab.Control.Text, origin.EndLine, block.Text);
        var at = new LspPosition(paste.Line, paste.Column);
        if (!tab.Control.TryApplyLspTextEdits([new LspTextEdit(new LspRange(at, at), paste.Text)], expectedVersion: null, out var error)) {
            ToastService.Info($"貼れませんでした: {error}");
            return;
        }
        browserTab.SearchOrigin = origin with { EndLine = paste.LastInsertedLine };
        // 貼ったところを選んで見せる（調べたときの選択——ビジュアルモードのまま——もこれで置き換わる）。
        tab.Control.SelectRange(paste.FirstInsertedLine, 0, paste.LastInsertedLine, int.MaxValue);
        FocusPane(PaneKind.Editor);
    }

    /// <summary>コマンドを調べ始めたターミナルのタブへ入れる（Enter は押さない）。前面で何か動いているタブには打たない
    /// ——Claude Code が居れば頼みごととして送られてしまう（§38）。</summary>
    private void SendCodeToSearchOriginTerminal(BrowserSearchOrigin.Terminal origin, BrowserCodeBlock block) {
        var command = BrowserPageCode.AsCommand(block.Text);
        if (RecentTerminalCommands.IsMultiline(command)) {
            InsertIntoComposer(command);
            return;
        }
        if (_terminalTabs.FirstOrDefault(t => t.Id == origin.TabId) is not { } tab)
            return;
        if (_paneActivity.IsTerminalBusy(tab.Id)) {
            ToastService.Info("そのタブは何かを動かしているので入れませんでした");
            return;
        }
        GoToTerminalTab(tab.Id);
        tab.View.SendTerminalInput(command);
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
            count: rows.Count > 0 ? rows.Count : null, note: rows.Count == 0 ? "このポートで待ち受けているプロセスはありません（サーバーが止まっている）。" : "");
    }
}
