namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ブラウザサポート（§41.3.3）。Azure DevOps の PR のページで、<b>未解決の指摘を手元の行へ</b>。
/// <para>PR のページで知りたいのは「どこを直せと言われているか」で、直すのは手元のエディタ。スレッドの一覧はページにも
/// あるが、ページからは手元のファイルのその行へ飛べない——飛べるのは部屋の中だけ。行の合わせ方・開く先の選び方は
/// §42 の部品（<see cref="ReviewWorktreeChooser"/>・<c>OpenReviewThreadLocallyAsync</c>）をそのまま使う。</para>
/// <para>開く先は PR のブランチのワークツリー（無ければ <c>pr/&lt;番号&gt;</c>）。そこで開いたファイルには §42 の吹き出しも
/// 出る。どちらも無ければ「ワークツリーを作る」——部屋は移らない（指摘の行を開くための置き場なので）。</para>
/// </summary>
public partial class ShellWindow {
    private const string ReviewSectionTitle = "未解決の指摘";

    private async Task LoadPullRequestReviewAsync(BrowserSupportPage page, string root, string remote) {
        var ado = page.AzureDevOps!;
        _browserSupport.SetSection(page, "review", ReviewSectionTitle, SectionReview, [], "PR のスレッドを読んでいます…");
        var detail = await ReviewDetailAsync(ado, refresh: true);
        if (!ReferenceEquals(_browserSupport.Page, page))
            return;
        if (detail is null) {
            _browserSupport.SetSection(page, "review", ReviewSectionTitle, SectionReview, [],
                "PR を読めませんでした（Azure DevOps の接続を確かめてください）。");
            return;
        }
        var threads = detail.Threads
            .Where(t => t.IsOpen && t.FilePath is not null && t.Line is > 0)
            .OrderBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Line)
            .ToList();
        if (threads.Count == 0) {
            _browserSupport.SetSection(page, "review", ReviewSectionTitle, SectionReview, [], "コードの行に付いた未解決の指摘はありません。");
            return;
        }

        var worktrees = await _git.For(root).GetWorktreesAsync();
        if (!ReferenceEquals(_browserSupport.Page, page))
            return;
        var worktree = ReviewWorktreeChooser.Choose(worktrees, detail.Summary.SourceBranch, detail.Summary.Id);
        var main = worktrees.FirstOrDefault(w => w.IsMain);
        var openIn = worktree?.Path ?? main?.Path ?? root;
        var rows = threads.Select(thread => {
            var relative = thread.FilePath!.TrimStart('/');
            var line = thread.Line!.Value;
            return new BrowserSupportRow($"{relative}:{line}", ThreadSummary(thread),
                [new BrowserSupportButton(worktree is null ? "本体で開く" : "開く",
                    () => _ = OpenReviewThreadLocallyAsync(openIn, relative, line, detail.SourceCommit),
                    worktree is null ? $"本体（ブランチ {main?.Branch ?? "?"}）のそのファイル。PR の版と違うことがある" : null)],
                BrowserSupportRowLook.Path);
        }).ToList();
        var note = worktree is not null
            ? $"{Path.GetFileName(worktree.Path.TrimEnd('\\', '/'))}（{worktree.Branch}）で開きます。開いたファイルの行にも指摘が出ます。"
            : $"PR のブランチ {detail.Summary.SourceBranch} のワークツリーがありません。作ると PR の版で開け、行に指摘が出ます。";
        var section = _browserSupport.SetSection(page, "review", ReviewSectionTitle, SectionReview, rows, note, count: threads.Count);
        if (section is null)
            return;
        section.Buttons.Clear();
        if (worktree is null)
            section.Buttons.Add(new BrowserSupportButton("ワークツリーを作る",
                () => _ = CreateReviewWorktreeAsync(page, root, remote, detail),
                $"ブランチ pr/{detail.Summary.Id} を PR の先頭から作ってワークツリーに置く（部屋は移らない・今の作業ツリーには触らない）"));
    }

    /// <summary>行に添える一言：最初の発言者と本文の頭（返信があれば数）。</summary>
    private static string ThreadSummary(AzureDevOpsThread thread) {
        if (thread.Comments.Count == 0)
            return "";
        var first = thread.Comments[0];
        var text = string.Join(" ", ReviewThreadLocator.MarkdownToText(first.Content).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 140)
            text = text[..140] + "…";
        var replies = thread.Comments.Count - 1;
        return $"{first.Author}: {text}" + (replies > 0 ? $"（返信 {replies}）" : "");
    }

    /// <summary>PR の先頭から <c>pr/&lt;番号&gt;</c> を作ってワークツリーに置く（パレットの「ワークツリーで開く」と同じ名前・置き場）。
    /// パレットのほうは部屋を移るが、ここは指摘の行を開くための置き場なので移らない。リポジトリは Git の対象とは限らないので
    /// そのクローンの窓口（<see cref="GitService.For"/>）で動かす。</summary>
    private async Task CreateReviewWorktreeAsync(BrowserSupportPage page, string root, string remote, AzureDevOpsPullRequestDetail detail) {
        var id = detail.Summary.Id;
        var git = _git.For(root);
        _browserSupport.ReportStatus($"PR {id} の先頭を {remote} から取ってきています…");
        var fetch = await git.RunAsync("fetch", "--no-tags", remote, detail.Summary.SourceRefName);
        var head = fetch.Success ? await git.RunAsync("rev-parse", "FETCH_HEAD") : fetch;
        if (!head.Success) {
            _browserSupport.ReportStatus($"PR {id} を取ってこられませんでした: {head.Message}");
            return;
        }
        var branch = $"pr/{id}";
        var exists = (await git.RunAsync("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Success;
        if (exists) {
            // どこにも置かれていない古い pr/<番号>（置かれていれば指摘の節がそこを選んでいる）：PR の今の先頭へ合わせ直す。
            // ただし PR に無いコミット（手元で直したもの）を持っていれば動かさない——動かすと reflog にしか残らない。
            if (await PullRequestBranchBlockerAsync(git, branch, head.Output.Trim()) is { } blocked) {
                _browserSupport.ReportStatus(blocked);
                return;
            }
            var moved = await git.RunAsync("branch", "-f", branch, head.Output.Trim());
            if (!moved.Success) {
                _browserSupport.ReportStatus($"ブランチ {branch} を更新できませんでした: {moved.Message}");
                return;
            }
        }
        var main = (await git.GetWorktreesAsync()).FirstOrDefault(w => w.IsMain)?.Path ?? root;
        var path = GitWorktreeArgs.SuggestPath(main, $"pr-{id}");
        var added = await git.AddWorktreeAsync(new GitWorktreeAddRequest(
            exists ? GitWorktreeAddMode.ExistingBranch : GitWorktreeAddMode.NewBranch, path, branch, exists ? null : head.Output.Trim()));
        if (!added.Success) {
            _browserSupport.ReportStatus($"ワークツリーを作れませんでした: {added.Message}");
            return;
        }
        _browserSupport.ReportStatus($"ワークツリー {path}（{branch}）を作りました。");
        if (ReferenceEquals(_browserSupport.Page, page))
            await LoadPullRequestReviewAsync(page, root, remote);
    }
}
