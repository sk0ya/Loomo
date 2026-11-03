using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザサポート（§41）のサイトごとの中身。Azure DevOps（PR・ファイル・パイプライン）、
/// GitHub（PR・Issue・ファイル・Actions）、CI のエラー → 手元のファイル、npm／NuGet のパッケージ → このワークスペースでの使われ方。
/// PR の指摘を手元の行で見るのは ShellWindow.BrowserSupportReview.cs。
/// <para><b>読むだけ</b>。Azure DevOps は共有の PAT（§24.27.1）、GitHub は gh の認証を使い、どちらへも書き込まない。
/// 手元への書き込みは、パッケージの追加・版の変更を<b>見えるターミナル</b>に打つことだけ（押したときだけ・出力が人に見える）。</para>
/// </summary>
public partial class ShellWindow {
    // ── Azure DevOps ───────────────────────────────────────────────

    private async Task RunAzureDevOpsSupportAsync(BrowserSupportActionKind kind, BrowserSupportPage page, AzureDevOpsWebPage ado, string? root) {
        switch (kind) {
            case BrowserSupportActionKind.PullRequestDiff when root is not null:
                // クローン探しはパレットからの差分と同じ（いまの対象→全リポジトリ。見つかればそれを Git の対象にして取る）。
                if (await ReadBrowserSupportPullRequestAsync(ado) is { } diffDetail)
                    await OpenAzurePullRequestDiffAsync(diffDetail.Summary);
                break;
            case BrowserSupportActionKind.PullRequestToClaude:
                if (await ReadBrowserSupportPullRequestAsync(ado) is { } detail)
                    await HandToClaudeAsync($"{ado.Project}-{ado.Repository}-PR-{detail.Summary.Id}.md", AzureDevOpsPullRequestJson.Markdown(detail));
                break;
            case BrowserSupportActionKind.OpenLocalFile when root is not null:
                await OpenBrowserSupportLocalFileAsync(AzureDevOpsWebUrl.LocalPath(root, ado.FilePath), ado.FilePath, ado.Line, ado.VersionBranch);
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

    /// <summary>パイプラインの実行の失敗（§41.5）：失敗した段と、そのエラーの場所。場所は issue に書かれていればそれ、
    /// 無ければ失敗した段のログ（先頭の 5 本まで）から拾い、ワークスペースのどれかのクローンのファイルへ直す。</summary>
    private async Task LoadAzureBuildFailuresAsync(BrowserSupportPage page) {
        var ado = page.AzureDevOps!;
        _browserSupport.SetSection(page, "problems", "エラー", SectionProblems, [], "失敗した段を読んでいます…");
        if (_vm.WorkItems is not { } workItems)
            return;
        IReadOnlyList<AzureDevOpsBuildStep> steps;
        var logs = new List<(AzureDevOpsBuildStep Step, string Text)>();
        try {
            var connection = await workItems.ConnectAsync(CancellationToken.None);
            steps = await workItems.Client.GetBuildFailuresAsync(ado.OrganizationUrl, ado.Project, ado.BuildId, connection.Auth, CancellationToken.None);
            foreach (var step in steps.Where(s => s.Failed && s.LogId > 0 && s.RecordType == "Task").Take(5))
                logs.Add((step, await workItems.Client.GetBuildLogAsync(ado.OrganizationUrl, ado.Project, ado.BuildId, step.LogId,
                    connection.Auth, CancellationToken.None)));
        }
        catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException) {
            _browserSupport.SetSection(page, "problems", "エラー", SectionProblems, [], $"読めませんでした: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(_browserSupport.Page, page))
            return;

        var problems = steps.SelectMany(s => s.Issues)
            .Select(issue => issue.SourcePath is { } path
                ? new CiLogProblem(path, issue.Line, issue.Column, issue.Type, issue.Message)
                : CiLogProblems.TryParseLine(issue.Message) ?? new CiLogProblem("", 0, 0, issue.Type, issue.Message))
            .Concat(logs.SelectMany(l => CiLogProblems.Extract(l.Text)))
            .DistinctBy(p => (p.Path, p.Line, p.Column, p.Message))
            .ToList();
        await ShowCiProblemsAsync(page, problems, root: null);
    }

    // ── GitHub ─────────────────────────────────────────────────────

    private async Task RunGitHubSupportAsync(BrowserSupportActionKind kind, BrowserSupportPage page, GitHubWebPage github, string? root) {
        if (kind is BrowserSupportActionKind.PullRequestDiff or BrowserSupportActionKind.PullRequestToClaude
                or BrowserSupportActionKind.IssueToClaude
            && !GitHubCli.IsInstalled) {
            _browserSupport.ReportStatus($"gh（GitHub CLI）が要ります。ターミナルで {GitHubCli.InstallCommand} を実行し、gh auth login でログインしてください。");
            return;
        }
        var folder = GhFolder(root);
        try {
            switch (kind) {
                case BrowserSupportActionKind.PullRequestDiff when root is not null && _browserSupportRemote is { } remote: {
                    var detail = await _gitHub.GetPullRequestAsync(folder, github.Number, github.Slug);
                    // クローンは分かっているので Git の対象にしてから取る（PR の取得・Diff ペインはその対象で動く）。
                    SelectGitRepository(root);
                    if (await FetchPullRequestAsync(remote, $"refs/pull/{github.Number}/head", $"refs/heads/{detail.Summary.BaseRef}",
                            $"#{github.Number}", ShowGitHubError) is { } fetched)
                        ShowPullRequestDiff(fetched, detail.Summary.BaseRef, $"#{github.Number}");
                    break;
                }
                case BrowserSupportActionKind.PullRequestToClaude:
                    await HandToClaudeAsync($"{github.Slug}-PR-{github.Number}.md",
                        GitHubMarkdown.PullRequest(await _gitHub.GetPullRequestAsync(folder, github.Number, github.Slug)));
                    break;
                case BrowserSupportActionKind.IssueToClaude:
                    await HandToClaudeAsync($"{github.Slug}-Issue-{github.Number}.md",
                        GitHubMarkdown.Issue(await _gitHub.GetIssueAsync(folder, github.Number, github.Slug)));
                    break;
                case BrowserSupportActionKind.OpenLocalFile when root is not null:
                    await OpenBrowserSupportLocalFileAsync(GitHubWebUrl.LocalPath(root, github.BlobPath), github.BlobPath, github.Line, null);
                    break;
            }
        }
        catch (Exception ex) when (ex is GitHubCliException or JsonException) {
            _browserSupport.ReportStatus($"gh で読めませんでした: {ex.Message}");
        }
    }

    /// <summary>gh を動かす場所。<c>--repo</c> で名指しするので、クローンが無ければどこでもよい（ワークスペース・一時フォルダー）。</summary>
    private string GhFolder(string? root) => root ?? _workspace.PrimaryFolder ?? Path.GetTempPath();

    /// <summary>GitHub Actions の実行の失敗：<c>gh run view --log-failed</c> からエラーの場所を拾う。</summary>
    private async Task LoadGitHubRunFailuresAsync(BrowserSupportPage page) {
        var github = page.GitHub!;
        if (!GitHubCli.IsInstalled) {
            _browserSupport.SetSection(page, "problems", "エラー", SectionProblems, [],
                $"失敗したログを読むには gh（GitHub CLI）が要ります（{GitHubCli.InstallCommand}）。");
            return;
        }
        _browserSupport.SetSection(page, "problems", "エラー", SectionProblems, [], "失敗したログを読んでいます…");
        string text;
        try {
            text = await _gitHub.GetFailedRunLogAsync(GhFolder(_browserSupport.RepositoryRoot), github.RunId, github.Slug);
        }
        catch (GitHubCliException ex) {
            _browserSupport.SetSection(page, "problems", "エラー", SectionProblems, [], $"読めませんでした: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(_browserSupport.Page, page))
            return;
        await ShowCiProblemsAsync(page, CiLogProblems.Extract(text), _browserSupport.RepositoryRoot);
    }

    // ── CI の共通 ──────────────────────────────────────────────────

    /// <summary>CI のエラーを、手元のファイルへ直して並べる。エージェントの上のパスは、ページのリポジトリのクローン
    /// （分からなければワークスペースの全リポジトリ）の下で後ろから一番長く一致するファイルへ（<see cref="CiLogProblems.MapToClone"/>）。</summary>
    private async Task ShowCiProblemsAsync(BrowserSupportPage page, IReadOnlyList<CiLogProblem> problems, string? root) {
        IReadOnlyList<string> roots = root is not null ? [root] : (await Task.Run(() => _git.Repositories.Repositories)).Select(r => r.Root).ToList();
        var mapped = await Task.Run(() => problems.Select(problem => (problem,
            Local: problem.Path.Length == 0 ? null : roots.Select(r => CiLogProblems.MapToClone(r, problem.Path)).FirstOrDefault(p => p is not null)))
            .ToList());
        var rows = mapped
            .OrderBy(p => p.problem.Severity == "error" ? 0 : 1)
            .Select(p => {
                var place = p.problem.Path.Length == 0 ? "" : p.problem.Line > 0 ? $"{p.problem.Path}:{p.problem.Line}" : p.problem.Path;
                var title = p.Local is { } local ? (p.problem.Line > 0 ? $"{_workspace.ToDisplayPath(local)}:{p.problem.Line}" : _workspace.ToDisplayPath(local)) : place;
                var message = (p.problem.Severity == "warning" ? "⚠ " : "✗ ") + Shorten(p.problem.Message, 300)
                    + (p.Local is null && place.Length > 0 ? "（手元に見つかりません）" : "");
                return new BrowserSupportRow(title.Length > 0 ? title : "（場所なし）", message,
                    p.Local is { } file ? [new BrowserSupportButton("開く", () => _ = OpenPathInEditorAsync(file, p.problem.Line, p.problem.Column))] : [],
                    BrowserSupportRowLook.Path);
            }).ToList();
        _browserSupport.SetSection(page, "problems", "エラー", SectionProblems, rows,
            count: rows.Count > 0 ? rows.Count : null, note: rows.Count == 0 ? "ログからエラーの場所は拾えませんでした。どの段が失敗したかと全文は、ページのログで確かめてください。" : "");

        static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;
    }

    // ── リポジトリの共通 ───────────────────────────────────────────

    private async Task OpenBrowserSupportLocalFileAsync(string? path, string pagePath, int line, string? pageBranch) {
        if (path is null || !File.Exists(path)) {
            _browserSupport.ReportStatus(pageBranch is { } branch
                ? $"手元に {pagePath} がありません（ページはブランチ {branch} のもの。手元のブランチには無いのかもしれません）。"
                : $"手元に {pagePath} がありません（手元のブランチには無いのかもしれません）。");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await OpenPathInEditorAsync(path, line, 0);
    }

    // ── npm／NuGet ─────────────────────────────────────────────────

    /// <summary>パッケージのページ：このワークスペースで使っている場所（版・開く・ページの版にする）と、まだ入れていない
    /// プロジェクト（追加）。追加・版の変更は見えるターミナルに打つ（<see cref="BrowserPackagePages.InstallCommand"/>）。</summary>
    private async Task LoadPackageUsagesAsync(BrowserSupportPage page, PackagePage package) {
        _browserSupport.SetSection(page, "usages", "このワークスペースで使っている場所", SectionUsages, [], "探しています…");
        var folders = _workspace.Folders.ToList();
        var scan = await Task.Run(() => BrowserPackagePages.Scan(folders, package));
        if (!ReferenceEquals(_browserSupport.Page, page))
            return;
        var safe = BrowserPackagePages.IsSafeName(package.Name) && BrowserPackagePages.IsSafeVersion(package.Version);

        var usages = scan.Usages.Take(40).Select(usage => {
            var buttons = new List<BrowserSupportButton> {
                new("開く", () => _ = OpenPathInEditorAsync(usage.FilePath, usage.Line, 0)),
            };
            if (safe && package.Version is { } version && usage.CanSetVersion && usage.Version != version)
                buttons.Add(new($"{version} にする", () => RunPackageCommand(BrowserPackagePages.InstallCommand(package, usage.FilePath, version, usage.Section)),
                    "見えるターミナルで実行する"));
            var versionText = usage.Version.Length > 0 ? usage.Version : "版の指定なし";
            return new BrowserSupportRow(_workspace.ToDisplayPath(usage.FilePath), $"{versionText}（{usage.Section}）", buttons, BrowserSupportRowLook.Path);
        }).ToList();
        _browserSupport.SetSection(page, "usages", "このワークスペースで使っている場所", SectionUsages, usages,
            count: usages.Count > 0 ? scan.Usages.Count : null, note: usages.Count == 0 ? $"このワークスペースでは {package.Name} を使っていません。" : scan.Truncated ? "大きなワークスペースなので途中まで探しました。" : "");

        // まだ入れていないプロジェクトは、少ないときだけ（大きなワークスペースで何十行も並べない。追加先が多いならターミナルで打つ方が早い）。
        var tooMany = scan.OtherProjects.Count > MaxOtherProjects;
        var others = (tooMany ? (IReadOnlyList<string>)[] : scan.OtherProjects).Select(project => new BrowserSupportRow(_workspace.ToDisplayPath(project), "",
            safe ? [new BrowserSupportButton(package.Version is { } v ? $"{v} を追加" : "追加（最新）",
                () => RunPackageCommand(BrowserPackagePages.InstallCommand(package, project, package.Version)), "見えるターミナルで実行する")] : [],
            BrowserSupportRowLook.Path)).ToList();
        if (scan.OtherProjects.Count > 0)
            _browserSupport.SetSection(page, "others", "まだ入れていないプロジェクト", SectionOtherProjects, others,
                tooMany ? $"多いので並べません。入れるプロジェクトで {(package.Ecosystem == PackageEcosystem.Npm ? "npm install" : "dotnet add package")} を実行してください。" : "",
                count: scan.OtherProjects.Count);
        else
            _browserSupport.RemoveSection(page, "others");
    }

    private const int MaxOtherProjects = 5;

    private void RunPackageCommand(string command) {
        if (_terminal.TryRunInVisibleTerminal(command)) {
            SetPaneVisible(PaneKind.Terminal, true);
            _browserSupport.ReportStatus($"ターミナルで実行しました: {command}");
        }
        else {
            _browserSupport.ReportStatus($"ターミナルが使えないか、何か動いています。手で実行してください: {command}");
        }
    }
}
