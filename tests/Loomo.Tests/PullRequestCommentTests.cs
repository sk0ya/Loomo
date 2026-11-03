using System.IO;
using sk0ya.Loomo.Services;
using System.Text.Json;

namespace sk0ya.Loomo.Tests;

/// <summary>PR のレビューのコメントをエディタのその行に出す（§42）の純粋な部品。</summary>
public class PullRequestCommentTests
{
    // ── リモート → Azure DevOps のリポジトリ ─────────────────────────

    [Theory]
    [InlineData("https://org@dev.azure.com/org/Proj/_git/Repo", "https://dev.azure.com/org", "Proj", "Repo")]
    [InlineData("https://dev.azure.com/org/My%20Proj/_git/My%20Repo", "https://dev.azure.com/org", "My Proj", "My Repo")]
    [InlineData("https://org.visualstudio.com/DefaultCollection/Proj/_git/Repo", "https://org.visualstudio.com/DefaultCollection", "Proj", "Repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/Proj/Repo", "https://dev.azure.com/org", "Proj", "Repo")]
    [InlineData("org@vs-ssh.visualstudio.com:v3/org/Proj/Repo", "https://org.visualstudio.com", "Proj", "Repo")]
    [InlineData("https://tfs.example.com/tfs/Coll/Proj/_git/Repo", "https://tfs.example.com/tfs/Coll", "Proj", "Repo")]
    public void AzureRemote_ParsesHttpsAndSsh(string url, string organization, string project, string repository)
        => Assert.Equal(new AzureDevOpsRemoteTarget(organization, project, repository), AzureDevOpsRemote.TryParse(url));

    [Theory]
    [InlineData("https://github.com/o/r.git")]
    [InlineData("git@github.com:o/r.git")]
    [InlineData("")]
    public void AzureRemote_IgnoresOtherHosts(string url) => Assert.Null(AzureDevOpsRemote.TryParse(url));

    // ── ワークツリーとブランチ ───────────────────────────────────

    [Fact]
    public void WorkTreeHead_ReadsBranchOfEachWorktree()
    {
        var root = Directory.CreateTempSubdirectory("loomo-wt-").FullName;
        try
        {
            // 本体：.git がフォルダー（main）。ワークツリー：.git がファイルで gitdir の先に別の HEAD（feature/x）。
            var main = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "wt"));
            File.WriteAllText(Path.Combine(main, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(main, ".git", "worktrees", "wt", "HEAD"), "ref: refs/heads/feature/x\n");
            var wt = Path.Combine(root, "wt");
            Directory.CreateDirectory(Path.Combine(wt, "src"));
            File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../repo/.git/worktrees/wt\n");
            Directory.CreateDirectory(Path.Combine(main, "src"));

            Assert.Equal(new GitWorkTreeHead(main, "main"), GitWorkTreeHeads.Find(Path.Combine(main, "src", "a.cs")));
            Assert.Equal(new GitWorkTreeHead(wt, "feature/x"), GitWorkTreeHeads.Find(Path.Combine(wt, "src", "a.cs")));
            Assert.Null(GitWorkTreeHeads.BranchFromHead("3f2a9c0d1e"));   // 切り離した HEAD
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── 行の付け直し ─────────────────────────────────────────────

    [Fact]
    public void LineAnchor_FollowsContentWhenLocalMoved()
    {
        string[] pr = ["using A;", "class C", "{", "    void M()", "    {", "        Foo();", "    }", "}"];
        // 手元では頭に2行足した：Foo() は 6 → 8 行目。
        string[] local = ["using A;", "using B;", "// note", "class C", "{", "    void M()", "    {", "        Foo();", "    }", "}"];
        Assert.Equal(new ReviewLineMatch(8, true), ReviewLineAnchor.Map(6, pr, local));
        // ありふれた行（"    }"）も前後の行で見分ける。
        Assert.Equal(new ReviewLineMatch(9, true), ReviewLineAnchor.Map(7, pr, local));
    }

    [Fact]
    public void LineAnchor_FallsBackToLineNumber()
    {
        string[] local = ["a", "b", "c"];
        Assert.Equal(new ReviewLineMatch(2, false), ReviewLineAnchor.Map(2, null, local));          // PR の版が手元に無い
        Assert.Equal(new ReviewLineMatch(2, false), ReviewLineAnchor.Map(2, ["x", "gone", "y"], local)); // 消えた行
        Assert.Equal(new ReviewLineMatch(3, false), ReviewLineAnchor.Map(9, null, local));          // 範囲に収める
    }

    // ── コメントの本文 ───────────────────────────────────────────

    [Fact]
    public void CommentText_SplitsImages()
    {
        var parts = ReviewCommentText.Split(
            "見て\n![image.png](https://dev.azure.com/org/_apis/git/repositories/r/pullRequests/1/attachments/image.png)\nと<img src=\"https://dev.azure.com/org/x.png\" alt=\"y\">です");
        Assert.Equal(["見て\n", "image.png", "\nと", "", "です"], parts.Select(p => p.Text));
        Assert.Equal([null, "https://dev.azure.com/org/_apis/git/repositories/r/pullRequests/1/attachments/image.png", null,
            "https://dev.azure.com/org/x.png", null], parts.Select(p => p.ImageUrl));
    }

    [Fact]
    public void Attachment_OnlyUnderTheOrganizationGetsThePat()
    {
        Assert.True(AzureDevOpsWorkItemClient.IsUnderOrganization("https://dev.azure.com/org", "https://dev.azure.com/org/_apis/x.png"));
        Assert.False(AzureDevOpsWorkItemClient.IsUnderOrganization("https://dev.azure.com/org", "https://dev.azure.com/other/_apis/x.png"));
        Assert.False(AzureDevOpsWorkItemClient.IsUnderOrganization("https://dev.azure.com/org", "https://evil.example.com/org/x.png"));
        Assert.False(AzureDevOpsWorkItemClient.IsUnderOrganization("https://dev.azure.com/org", "http://dev.azure.com/org/x.png"));
    }

    // ── スレッドの読み・ページの URL ────────────────────────────────

    [Fact]
    public void Threads_CarryIdStatusAndSourceCommit()
    {
        using var pr = JsonDocument.Parse("""{"pullRequestId":7,"lastMergeSourceCommit":{"commitId":"abc123"}}""");
        using var threads = JsonDocument.Parse("""
            {"value":[
              {"id":11,"status":"active","threadContext":{"filePath":"/src/a.cs","rightFileStart":{"line":5}},
               "comments":[{"content":"ここ","author":{"displayName":"A"}}]},
              {"id":12,"status":"fixed","comments":[{"content":"全体","author":{"displayName":"B"}}]}]}
            """);
        var detail = AzureDevOpsPullRequestJson.ParseDetail(pr.RootElement, threads.RootElement, "https://dev.azure.com/org", new("P", "R"));
        Assert.Equal("abc123", detail.SourceCommit);
        Assert.Equal([(11, true, "/src/a.cs", (int?)5), (12, false, (string?)null, (int?)null)],
            detail.Threads.Select(t => (t.Id, t.IsOpen, t.FilePath, t.Line)));
        Assert.Equal("https://x/pullrequest/7?_a=files&path=%2Fsrc%2Fa.cs&discussionId=11",
            sk0ya.Loomo.App.Services.PullRequestCommentController.ThreadUrl("https://x/pullrequest/7", detail.Threads[0]));
    }

    // ── ページの右クリック → スレッド・ワークツリー（§42.3） ──────────────

    private static AzureDevOpsThread Thread(int id, string content, string path = "/src/a.cs", int line = 3)
        => new(path, line, "active", [new AzureDevOpsComment("A", content, null)], id);

    [Fact]
    public void Locator_FindsThreadFromRenderedText()
    {
        var threads = new[]
        {
            Thread(1, "**ここ**は `null` を返すと落ちます。[詳しく](https://x)を見て"),
            Thread(2, "名前をもっと具体的に"),
        };
        // 内側の段（コメント本文）で1つに決まる。Markdown の記号・リンクは表示では消えている。
        Assert.Equal(1, ReviewThreadLocator.Find(["ここは null を返すと落ちます。詳しくを見て", "A\nここは null を返すと落ちます。…"], threads)!.Id);
        // 外側すぎて2つとも含む段しか無ければ決めない（URL の discussionId があればそれ）。
        string[] outer = ["ここは null を返すと落ちます。詳しくを見て 名前をもっと具体的に"];
        Assert.Null(ReviewThreadLocator.Find(outer, threads));
        Assert.Equal(2, ReviewThreadLocator.Find(outer, threads, discussionId: 2)!.Id);
        Assert.Null(ReviewThreadLocator.Find(["関係ない文字"], threads));
    }

    [Fact]
    public void WorktreeChooser_PrefersSourceBranchThenPrWorktree()
    {
        var main = new GitWorktreeInfo { Path = @"C:
", Branch = "main", IsMain = true };
        var feature = new GitWorktreeInfo { Path = @"C:
-feature", Branch = "feature/x" };
        var review = new GitWorktreeInfo { Path = @"C:
-pr-7", Branch = "pr/7" };
        Assert.Same(feature, ReviewWorktreeChooser.Choose([main, review, feature], "feature/x", 7));
        Assert.Same(review, ReviewWorktreeChooser.Choose([main, review], "feature/x", 7));
        Assert.Null(ReviewWorktreeChooser.Choose([main], "feature/x", 7));   // 黙って本体を選ばない
        Assert.Null(ReviewWorktreeChooser.Choose([main, feature with { IsPrunable = true }], "feature/x", 7));
    }
}
