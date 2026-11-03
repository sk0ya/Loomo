using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザサポート（§41）のサイトごとの中身。Azure DevOps（PR・ファイル・コミット・パイプライン）、
/// GitHub（PR・Issue・ファイル・コミット・Actions）、CI のエラー → 手元のファイル、npm／NuGet のパッケージ → このワークスペースでの使われ方。
/// <para><b>読むだけ</b>。Azure DevOps は共有の PAT（§24.27.1）、GitHub は gh の認証を使い、どちらへも書き込まない。
/// 手元への書き込みは、パッケージの追加・版の変更を<b>見えるターミナル</b>に打つことだけ（押したときだけ・出力が人に見える）。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>いま見ているページの CI の失敗したログ（「ログを読む」で写しにする。読み直さない）。</summary>
    private (BrowserSupportPage Page, string Text)? _browserSupportLog;

    // ── Azure DevOps ───────────────────────────────────────────────

    private async Task RunAzureDevOpsSupportAsync(BrowserSupportActionKind kind, BrowserSupportPage page, AzureDevOpsWebPage ado, string? root) {
        switch (kind) {
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
                await OpenBrowserSupportLocalFileAsync(AzureDevOpsWebUrl.LocalPath(root, ado.FilePath), ado.FilePath, ado.Line, ado.VersionBranch);
                break;
            case BrowserSupportActionKind.CommitDiff when root is not null:
                await OpenBrowserSupportCommitAsync(ado.CommitHash, root);
                break;
            case BrowserSupportActionKind.BuildLog:
                if (_browserSupportLog is { } log && ReferenceEquals(log.Page, page))
                    await OpenBrowserSupportLogAsync($"build-{ado.BuildId}.log", log.Text);
                else
                    _browserSupport.ReportStatus("失敗したログはまだ読めていません（読み終わると押せます）。");
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
        _browserSupportLog = (page, string.Join("\n\n", logs.Select(l => $"===== {l.Step.Name} =====\n{l.Text}")));

        var problems = steps.SelectMany(s => s.Issues)
            .Select(issue => issue.SourcePath is { } path
                ? new CiLogProblem(path, issue.Line, issue.Column, issue.Type, issue.Message)
                : CiLogProblems.TryParseLine(issue.Message) ?? new CiLogProblem("", 0, 0, issue.Type, issue.Message))
            .Concat(logs.SelectMany(l => CiLogProblems.Extract(l.Text)))
            .DistinctBy(p => (p.Path, p.Line, p.Column, p.Message))
            .ToList();
        await ShowCiProblemsAsync(page, problems, root: null);

        var failed = steps.Where(s => s.Failed && s.RecordType is "Task" or "Job").OrderBy(s => s.Order).Select(step =>
            new BrowserSupportRow(step.Name, step.RecordType == "Job" ? "ジョブ" : "タスク",
                step.LogId > 0 ? [new BrowserSupportButton("ログを読む", () => _ = OpenAzureBuildStepLogAsync(ado, step))] : [])).ToList();
        _browserSupport.SetSection(page, "steps", "失敗した段", SectionFailedSteps, failed,
            failed.Count == 0 ? "失敗した段はありません。" : "");
    }

    private async Task OpenAzureBuildStepLogAsync(AzureDevOpsWebPage ado, AzureDevOpsBuildStep step) {
        if (_vm.WorkItems is not { } workItems)
            return;
        try {
            var connection = await workItems.ConnectAsync(CancellationToken.None);
            var text = await workItems.Client.GetBuildLogAsync(ado.OrganizationUrl, ado.Project, ado.BuildId, step.LogId, connection.Auth, CancellationToken.None);
            await OpenBrowserSupportLogAsync($"build-{ado.BuildId}-{step.LogId}.log", text);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException) {
            _browserSupport.ReportStatus($"ログを読めませんでした: {ex.Message}");
        }
    }

    // ── GitHub ─────────────────────────────────────────────────────

    private async Task RunGitHubSupportAsync(BrowserSupportActionKind kind, BrowserSupportPage page, GitHubWebPage github, string? root) {
        if (kind is BrowserSupportActionKind.PullRequestDiff or BrowserSupportActionKind.PullRequestDocument
                or BrowserSupportActionKind.IssueDocument or BrowserSupportActionKind.BuildLog
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
                case BrowserSupportActionKind.PullRequestDocument:
                    await OpenGitHubDocumentAsync($"PR-{github.Number}.md",
                        GitHubMarkdown.PullRequest(await _gitHub.GetPullRequestAsync(folder, github.Number, github.Slug)));
                    break;
                case BrowserSupportActionKind.IssueDocument:
                    await OpenGitHubDocumentAsync($"Issue-{github.Number}.md",
                        GitHubMarkdown.Issue(await _gitHub.GetIssueAsync(folder, github.Number, github.Slug)));
                    break;
                case BrowserSupportActionKind.OpenLocalFile when root is not null:
                    await OpenBrowserSupportLocalFileAsync(GitHubWebUrl.LocalPath(root, github.BlobPath), github.BlobPath, github.Line, null);
                    break;
                case BrowserSupportActionKind.CommitDiff when root is not null:
                    await OpenBrowserSupportCommitAsync(github.CommitSha, root);
                    break;
                case BrowserSupportActionKind.BuildLog: {
                    var text = _browserSupportLog is { } log && ReferenceEquals(log.Page, page)
                        ? log.Text : await _gitHub.GetFailedRunLogAsync(folder, github.RunId, github.Slug);
                    await OpenBrowserSupportLogAsync($"run-{github.RunId}.log", text);
                    break;
                }
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
        _browserSupportLog = (page, text);
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
            count: rows.Count > 0 ? rows.Count : null, note: rows.Count == 0 ? "ログからエラーの場所は拾えませんでした。「失敗したログをエディタで読む」で全文を見られます。" : "");

        static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;
    }

    private async Task OpenBrowserSupportLogAsync(string fileName, string text) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            FileName = fileName,
            Content = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            OnSaved = _ => { },   // 読むための写し
        });
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

    /// <summary>ページのコミットを Diff ペインで。手元に無ければ、そのリポジトリを指すリモートから取ってくる。</summary>
    private async Task OpenBrowserSupportCommitAsync(string commit, string root) {
        var git = _git.For(root);
        var hash = await ResolveCommitAsync(git, commit);
        if (hash is null && _browserSupportRemote is { } remote) {
            _browserSupport.ReportStatus($"コミット {commit[..Math.Min(7, commit.Length)]} を {remote} から取ってきています…");
            var fetch = await git.RunAsync("fetch", "--no-tags", remote);
            if (!fetch.Success) {
                _browserSupport.ReportStatus($"{remote} から取ってこられませんでした: {fetch.Message}");
                return;
            }
            hash = await ResolveCommitAsync(git, commit);
        }
        if (hash is null) {
            _browserSupport.ReportStatus($"コミット {commit} が手元にもリモートにも見つかりません。");
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

        var others = scan.OtherProjects.Take(30).Select(project => new BrowserSupportRow(_workspace.ToDisplayPath(project), "",
            safe ? [new BrowserSupportButton(package.Version is { } v ? $"{v} を追加" : "追加（最新）",
                () => RunPackageCommand(BrowserPackagePages.InstallCommand(package, project, package.Version)), "見えるターミナルで実行する")] : [],
            BrowserSupportRowLook.Path)).ToList();
        if (others.Count > 0)
            _browserSupport.SetSection(page, "others", "まだ入れていないプロジェクト", SectionOtherProjects, others, count: scan.OtherProjects.Count);
        else
            _browserSupport.RemoveSection(page, "others");
    }

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
