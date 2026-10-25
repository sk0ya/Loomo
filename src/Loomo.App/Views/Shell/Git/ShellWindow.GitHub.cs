using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: GitHub の PR／Issue を部屋の中で扱う（§24.27）。それまでは Git ペインの見出しから
/// ブラウザで github.com を開くだけで、PR を読む・差分を見る・手元で動かす・レビューするのは部屋の外だった。
/// <list type="bullet">
/// <item>一覧はパレット（「GitHub: プルリクエスト」「GitHub: Issue」）。選ぶと、その PR でできることを並べ直す。</item>
/// <item>概要と会話は Markdown の写しとしてエディタで開く（プレビューで読める・素材として送れる）。</item>
/// <item>差分は Diff ペインの2点比較（分岐点から）＝ GitHub の「Files changed」と同じ見え方。</item>
/// <item>手元で動かすのは「チェックアウト」（今の作業ツリー）か「ワークツリーで開く」（別の部屋）。</item>
/// <item>レビュー・コメントは gh で送る。行コメントは持たない（§24.27 の非目標）。</item>
/// </list>
/// gh の認証をそのまま使い、Loomo は資格情報を預からない。gh が無ければ入れ方を出す。
/// </summary>
public partial class ShellWindow
{
    private const string GitHubCategory = "GitHub";
    private readonly GitHubCli _gitHub = new();

    private void OnGitHubPullRequestsMenu(object sender, RoutedEventArgs e) => OpenGitHubPullRequests();

    /// <summary>ブランチ一覧の PR の印（§24.39）。gh が無ければ引かない（入れ方は PR の一覧を開いたときに出す）。</summary>
    private void InitializeBranchPullRequests()
    {
        _vm.GitSession.PullRequestSource = folder => GitHubCli.IsInstalled
            ? _gitHub.ListPullRequestsAsync(folder)
            : Task.FromResult<IReadOnlyList<GitHubPullRequest>>(Array.Empty<GitHubPullRequest>());
        _vm.GitSession.CommitBuildSource = LoadCommitBuildsAsync;
        _vm.GitSession.History.BuildOpenRequested += url => _ = OpenUrlInBrowserAsync(url, null);
        _vm.GitPanel.ConflictForecastOpenRequested += OpenConflictForecastPalette;
        _vm.GitSession.BranchPullRequestOpenRequested += pr => {
            if (_git.RootPath is { Length: > 0 } folder)
                OpenGitHubPullRequestActions(folder, pr);
        };
    }
    /// <summary>
    /// コミット一覧のビルドの印（§24.93）。GitHub のリポジトリなら gh で Actions の実行を、Azure Repos なら Work Items と同じ接続
    /// （設定の組織 URL・PAT の候補）で Pipelines のビルドを引く。どちらでもない・引けない（gh が無い・PAT が無い）なら空——
    /// 添え物なので窓も知らせも出さない。
    /// </summary>
    private async Task<IReadOnlyList<CommitBuild>> LoadCommitBuildsAsync(string folder, IReadOnlyList<GitRemoteInfo> remotes)
    {
        var ordered = remotes.OrderBy(r => string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        if (ordered.Any(r => GitHostingUrl.TryGetGitHubRepositoryUrl(r.Url, out _)))
            return GitHubCli.IsInstalled ? await _gitHub.ListRecentRunsAsync(folder) : Array.Empty<CommitBuild>();
        if (_vm.WorkItems is not { } workItems)
            return Array.Empty<CommitBuild>();
        foreach (var remote in ordered)
        {
            if (!GitHostingUrl.TryGetAzureReposTarget(remote.Url, out var project, out var repository))
                continue;
            try
            {
                var connection = await workItems.ConnectAsync(CancellationToken.None);
                return await workItems.Client.GetRecentBuildsAsync(
                    connection.OrganizationUrl, new AzureDevOpsPrTarget(project, repository), connection.Auth, CancellationToken.None);
            }
            catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                return Array.Empty<CommitBuild>();   // 未設定・PAT 無し・ネットワーク。黙って印を出さない
            }
        }
        return Array.Empty<CommitBuild>();
    }

    private void OnGitHubIssuesMenu(object sender, RoutedEventArgs e) => OpenGitHubIssues();

    /// <summary>分岐元とぶつかりそうなファイル（§24.40）の一覧。選ぶとそのファイルを分岐元と比べる。</summary>
    private void OpenConflictForecastPalette(GitConflictForecast forecast)
    {
        if (_git.RootPath is not { Length: > 0 } root) return;
        var items = forecast.Files.Select(path => new PaletteCommand("衝突しそう", path, () =>
            ShowDiff(new DiffOpenTarget.CompareBase(new GitCompareBaseSelection(GitCompareBaseKind.Branch, forecast.Target), forecast.Target)))
        {
            Target = new PaletteTarget(Path.Combine(root, path)),
            Detail = $"{forecast.Target} へマージすると衝突する（作業ツリーには触らずに確かめた）\nEnter で {forecast.Target} との差分を開く",
        }).ToList();
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private async void OpenGitHubPullRequests()
    {
        if (GitHubFolder() is not { } folder) return;
        var loading = ShowGitHubLoading("プルリクエストを読み込んでいます…");
        IReadOnlyList<GitHubPullRequest> pulls;
        try
        {
            pulls = await _gitHub.ListPullRequestsAsync(folder);
        }
        catch (Exception ex) when (ex is GitHubCliException or System.Text.Json.JsonException)
        {
            ReplaceGitHubPalette(loading, GitHubFailure("プルリクエストを読めませんでした", ex.Message, "pulls"));
            return;
        }

        var now = DateTimeOffset.Now;
        var items = pulls.Select(pr => new PaletteCommand(GitHubCategory, $"#{pr.Number} {pr.Title}",
            () => OpenGitHubPullRequestActions(folder, pr))
        {
            Badge = GitHubMarkdown.Badge(pr) is { Length: > 0 } badge ? badge : null,
            Detail = string.Join(Environment.NewLine, new[]
            {
                $"@{pr.Author}  {pr.HeadRef} → {pr.BaseRef}{(pr.IsCrossRepository ? "（フォークから）" : "")}",
                pr.UpdatedAt is { } at ? $"更新: {RelativeAge(now - at)}" : null,
                $"チェック: {GitHubMarkdown.ChecksLabel(pr.Checks)}",
                GitHubMarkdown.ReviewLabel(pr.ReviewDecision) is { } review ? $"レビュー: {review}" : null,
            }.Where(line => line is not null)),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(GitHubCategory, "オープンなプルリクエストはありません", () => { }));
        items.Add(OpenOnGitHubItem("pulls", "プルリクエストを github.com で開く"));
        ReplaceGitHubPalette(loading, items);
    }

    private void OpenGitHubPullRequestActions(string folder, GitHubPullRequest pr)
    {
        var title = $"#{pr.Number} {pr.Title}";
        PaletteCommand Act(string label, Action run, string? detail = null)
            => new(GitHubCategory, label, run) { Detail = detail ?? title };
        OpenCommandPalette(PaletteMode.Command, [
            Act("概要と会話を読む（エディタ）", () => _ = OpenGitHubPullRequestDocumentAsync(folder, pr)),
            Act("差分を見る（分岐点から）", () => _ = OpenGitHubPullRequestDiffAsync(pr),
                $"{title}\n{pr.BaseRef} から分かれたあとの変更だけ（GitHub の Files changed と同じ）"),
            Act("ワークツリーで開く（別の部屋）", () => _ = OpenGitHubPullRequestWorktreeAsync(pr),
                $"{title}\nブランチ pr/{pr.Number} を作ってワークツリーに置き、その部屋へ移る。今の作業ツリーには触らない"),
            Act("いまの作業ツリーでチェックアウト", () => _ = CheckoutGitHubPullRequestAsync(folder, pr),
                $"{title}\ngh pr checkout {pr.Number}（未コミットの変更があると gh が断る）"),
            Act("承認する…", () => _ = ReviewGitHubPullRequestAsync(folder, pr, GitHubReviewKind.Approve)),
            Act("変更を求める…", () => _ = ReviewGitHubPullRequestAsync(folder, pr, GitHubReviewKind.RequestChanges)),
            Act("コメントする…", () => _ = CommentOnGitHubAsync(folder, pr.Number, isIssue: false, title)),
            Act("ブラウザで開く", () => _ = OpenUrlInBrowserAsync(pr.Url, null), pr.Url),
        ]);
    }

    private async Task OpenGitHubPullRequestDocumentAsync(string folder, GitHubPullRequest pr)
    {
        try
        {
            var detail = await _gitHub.GetPullRequestAsync(folder, pr.Number);
            await OpenGitHubDocumentAsync($"PR-{pr.Number}.md", GitHubMarkdown.PullRequest(detail));
        }
        catch (Exception ex) when (ex is GitHubCliException or System.Text.Json.JsonException)
        {
            ShowGitHubError($"#{pr.Number} を読めませんでした", ex.Message);
        }
    }

    /// <summary>PR の先頭と基準ブランチをリモートから取り、分岐点からの2点比較を Diff ペインへ出す。
    /// PR の先頭は <c>refs/pull/N/head</c>（フォークの PR も元のリポジトリに載る）を ref を作らずに取る。</summary>
    private async Task OpenGitHubPullRequestDiffAsync(GitHubPullRequest pr)
    {
        if (await FetchGitHubPullRequestAsync(pr) is not { } fetched) return;
        ShowPullRequestDiff(fetched, pr.BaseRef, $"#{pr.Number}");
    }

    private async Task OpenGitHubPullRequestWorktreeAsync(GitHubPullRequest pr)
    {
        if (await FetchGitHubPullRequestAsync(pr) is not { } fetched) return;
        await OpenPullRequestWorktreeAsync($"pr/{pr.Number}", $"pr-{pr.Number}", fetched.HeadHash, ShowGitHubError);
    }

    private async Task<FetchedPullRequest?> FetchGitHubPullRequestAsync(GitHubPullRequest pr)
    {
        if (GitHubRemoteName() is not { } remote)
        {
            ShowGitHubError("GitHub のリモートが見つかりません", "git remote に github.com を指すリモートがありません。");
            return null;
        }
        return await FetchPullRequestAsync(remote, $"refs/pull/{pr.Number}/head", $"refs/heads/{pr.BaseRef}",
            $"#{pr.Number}", ShowGitHubError);
    }

    /// <summary>github.com を指すリモート（origin を優先）。gh が選ぶリポジトリと同じ順で選ぶ。</summary>
    private string? GitHubRemoteName()
        => _vm.GitSession.Remotes
            .OrderBy(remote => string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault(remote => GitHostingUrl.TryGetGitHubRepositoryUrl(remote.Url, out _))?.Name;

    private async Task CheckoutGitHubPullRequestAsync(string folder, GitHubPullRequest pr)
    {
        var result = await _gitHub.CheckoutAsync(folder, pr.Number);
        if (!result.Success)
            ShowGitHubError($"#{pr.Number} をチェックアウトできませんでした", result.Message);
        else
            EditorSharedStatusBar?.UpdateStatus($"#{pr.Number} をチェックアウトしました（{pr.HeadRef}）");
    }

    private async Task ReviewGitHubPullRequestAsync(string folder, GitHubPullRequest pr, GitHubReviewKind kind)
    {
        var (caption, prompt, allowEmpty) = kind switch
        {
            GitHubReviewKind.Approve => ("承認する", "承認に添えるコメント（空でもよい）:", true),
            GitHubReviewKind.RequestChanges => ("変更を求める", "何を直してほしいか:", false),
            _ => ("レビューコメント", "コメント:", false),
        };
        var body = InputDialog.Prompt(this, $"#{pr.Number} {caption}", prompt, allowEmpty: allowEmpty, multiline: true);
        if (body is null) return;
        var result = await _gitHub.ReviewAsync(folder, pr.Number, kind, body);
        if (!result.Success)
            ShowGitHubError($"#{pr.Number} にレビューを送れませんでした", result.Message);
        else
            EditorSharedStatusBar?.UpdateStatus($"#{pr.Number} に「{caption}」を送りました");
    }

    private async Task CommentOnGitHubAsync(string folder, int number, bool isIssue, string title)
    {
        var body = InputDialog.Prompt(this, $"{title} にコメント", "コメント（Markdown）:", multiline: true);
        if (string.IsNullOrWhiteSpace(body)) return;
        var result = await _gitHub.CommentAsync(folder, number, isIssue, body);
        if (!result.Success)
            ShowGitHubError($"#{number} にコメントを送れませんでした", result.Message);
        else
            EditorSharedStatusBar?.UpdateStatus($"#{number} にコメントしました");
    }

    // ── Issue ─────────────────────────────────────────────────────

    private async void OpenGitHubIssues()
    {
        if (GitHubFolder() is not { } folder) return;
        var loading = ShowGitHubLoading("Issue を読み込んでいます…");
        IReadOnlyList<GitHubIssue> issues;
        try
        {
            issues = await _gitHub.ListIssuesAsync(folder);
        }
        catch (Exception ex) when (ex is GitHubCliException or System.Text.Json.JsonException)
        {
            ReplaceGitHubPalette(loading, GitHubFailure("Issue を読めませんでした", ex.Message, "issues"));
            return;
        }

        var now = DateTimeOffset.Now;
        var items = issues.Select(issue => new PaletteCommand(GitHubCategory, $"#{issue.Number} {issue.Title}",
            () => OpenGitHubIssueActions(folder, issue))
        {
            Badge = issue.CommentCount > 0 ? $"💬 {issue.CommentCount}" : null,
            Detail = string.Join(Environment.NewLine, new[]
            {
                $"@{issue.Author}",
                issue.Labels.Count > 0 ? $"ラベル: {string.Join(", ", issue.Labels)}" : null,
                issue.UpdatedAt is { } at ? $"更新: {RelativeAge(now - at)}" : null,
            }.Where(line => line is not null)),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(GitHubCategory, "オープンな Issue はありません", () => { }));
        items.Add(OpenOnGitHubItem("issues", "Issue を github.com で開く"));
        ReplaceGitHubPalette(loading, items);
    }

    private void OpenGitHubIssueActions(string folder, GitHubIssue issue)
    {
        var title = $"#{issue.Number} {issue.Title}";
        OpenCommandPalette(PaletteMode.Command, [
            new(GitHubCategory, "この Issue で作業を始める（ブランチ＋ワークツリー）…",
                () => _ = StartWorkOnTicketAsync(new WorkTicket(WorkTicketSource.GitHub, issue.Number, issue.Title, issue.Url)))
            {
                Detail = $"{title}{Environment.NewLine}番号入りのブランチとワークツリーを作り、部屋として開く（望めば Claude Code も起動）",
            },
            new(GitHubCategory, "本文と会話を読む（エディタ）", () => _ = OpenGitHubIssueDocumentAsync(folder, issue)) { Detail = title },
            new(GitHubCategory, "コメントする…", () => _ = CommentOnGitHubAsync(folder, issue.Number, isIssue: true, title)) { Detail = title },
            new(GitHubCategory, "ブラウザで開く", () => _ = OpenUrlInBrowserAsync(issue.Url, null)) { Detail = issue.Url },
        ]);
    }

    private async Task OpenGitHubIssueDocumentAsync(string folder, GitHubIssue issue)
    {
        try
        {
            var detail = await _gitHub.GetIssueAsync(folder, issue.Number);
            await OpenGitHubDocumentAsync($"Issue-{issue.Number}.md", GitHubMarkdown.Issue(detail));
        }
        catch (Exception ex) when (ex is GitHubCliException or System.Text.Json.JsonException)
        {
            ShowGitHubError($"#{issue.Number} を読めませんでした", ex.Message);
        }
    }

    // ── 共通 ──────────────────────────────────────────────────────

    /// <summary>gh を動かす場所（Git の対象フォルダー）。gh が無ければ入れ方を出して null。</summary>
    private string? GitHubFolder()
    {
        if (!GitHubCli.IsInstalled)
        {
            OpenCommandPalette(PaletteMode.Command, [
                new PaletteCommand(GitHubCategory, "gh（GitHub CLI）をインストール（可視ターミナルで winget）", () =>
                {
                    if (!_terminal.TryRunInVisibleTerminal(GitHubCli.InstallCommand))
                        ShowGitHubError("ターミナルで実行できませんでした", $"手で実行してください: {GitHubCli.InstallCommand}");
                }) { Detail = $"{GitHubCli.InstallCommand}\n入れたら一度 gh auth login でログインしてください。Loomo は gh の認証をそのまま使います。" },
                OpenOnGitHubItem("pulls", "プルリクエストを github.com で開く"),
            ]);
            return null;
        }
        if (_git.RootPath is not { Length: > 0 } folder)
        {
            ShowGitHubError("フォルダーが開かれていません", "GitHub のリポジトリのフォルダーを開いてください。");
            return null;
        }
        return folder;
    }

    private IReadOnlyList<PaletteCommand> ShowGitHubLoading(string message)
    {
        IReadOnlyList<PaletteCommand> loading = [new PaletteCommand(GitHubCategory, message, () => { })];
        OpenCommandPalette(PaletteMode.Command, loading);
        return loading;
    }

    /// <summary>読み込み中の一覧を結果へ差し替える。待っている間に人がパレットを閉じた・別の一覧へ移ったなら、
    /// 開き直さない（閉じたものが勝手に戻ってこないように）。</summary>
    private void ReplaceGitHubPalette(IReadOnlyList<PaletteCommand> loading, IReadOnlyList<PaletteCommand> items)
    {
        if (IsPaletteOpen && ReferenceEquals(_paletteCommands, loading))
            OpenCommandPalette(PaletteMode.Command, items);
    }

    private List<PaletteCommand> GitHubFailure(string title, string reason, string page) =>
    [
        new PaletteCommand(GitHubCategory, title, () => { }) { Detail = reason },
        new PaletteCommand(GitHubCategory, "gh にログインする（可視ターミナルで gh auth login）",
            () => _terminal.TryRunInVisibleTerminal("gh auth login")) { Detail = "未ログインならこれで。Loomo は gh の認証をそのまま使います。" },
        OpenOnGitHubItem(page, "github.com で開く"),
    ];

    private PaletteCommand OpenOnGitHubItem(string page, string label)
        => new(GitHubCategory, label, () =>
        {
            if (_vm.GitSession.GitHubRepositoryUrl is { } url)
                _ = OpenUrlInBrowserAsync($"{url}/{page}", null);
        });

    private async Task OpenGitHubDocumentAsync(string fileName, string markdown)
    {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = fileName,
            Content = markdown,
            OnSaved = _ => { },   // 読むための写し：保存しても GitHub へは戻さない（コメントは別の操作で送る）
        });
    }

    private void ShowGitHubError(string title, string reason)
        => MessageBox.Show(this, string.IsNullOrWhiteSpace(reason) ? title : $"{title}\n\n{reason.Trim()}",
            "GitHub", MessageBoxButton.OK, MessageBoxImage.Warning);
}
