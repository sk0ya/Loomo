using System.Text.Json;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>Azure DevOps の PR を部屋の中で読む（§24.27.1）。応答の読み方・会話の並べ方・写し・リモートの照合（純粋）。</summary>
public class AzureDevOpsPullRequestTests
{
    private const string Org = "https://dev.azure.com/org";
    private static readonly AzureDevOpsPrTarget Target = new("Proj", "Repo");

    private const string PullRequestJson = """
        {"pullRequestId":12,"title":"機能 X","isDraft":false,"status":"active","mergeStatus":"conflicts",
         "createdBy":{"displayName":"山田"},"creationDate":"2026-10-17T01:00:00Z",
         "sourceRefName":"refs/heads/feature/x","targetRefName":"refs/heads/main","description":"説明",
         "workItemRefs":[{"id":"345"}],
         "reviewers":[{"displayName":"佐藤","vote":10,"isRequired":true},{"displayName":"鈴木","vote":-5}]}
        """;

    private const string ThreadsJson = """
        {"value":[
          {"id":2,"status":"active","threadContext":{"filePath":"/src/a.cs","rightFileStart":{"line":42}},
           "comments":[{"author":{"displayName":"佐藤"},"content":"ここ","publishedDate":"2026-10-17T03:00:00Z","commentType":"text"},
                       {"author":{"displayName":"山田"},"content":"直しました","publishedDate":"2026-10-17T04:00:00Z","commentType":"text"}]},
          {"id":1,"comments":[{"author":{"displayName":"system"},"content":"佐藤 voted 10","publishedDate":"2026-10-17T02:00:00Z","commentType":"system"}]},
          {"id":3,"isDeleted":true,"comments":[{"author":{"displayName":"x"},"content":"消した","commentType":"text"}]},
          {"id":4,"status":"fixed",
           "comments":[{"author":{"displayName":"鈴木"},"content":"全体の話","publishedDate":"2026-10-17T02:30:00Z","commentType":"text"}]}]}
        """;

    private static AzureDevOpsPullRequestDetail Detail()
    {
        using var pr = JsonDocument.Parse(PullRequestJson);
        using var threads = JsonDocument.Parse(ThreadsJson);
        return AzureDevOpsPullRequestJson.ParseDetail(pr.RootElement, threads.RootElement, Org, Target);
    }

    [Fact]
    public void PRの作成者とブランチを読む()
    {
        var pull = Detail().Summary;
        Assert.Equal(12, pull.Id);
        Assert.Equal("山田", pull.Author);
        Assert.Equal("feature/x", pull.SourceBranch);
        Assert.Equal("main", pull.TargetBranch);
        Assert.Equal(new[] { 345 }, pull.LinkedWorkItemIds);
        Assert.Equal("https://dev.azure.com/org/Proj/_git/Repo/pullrequest/12", pull.WebUrl);
    }

    [Fact]
    public void 会話は人の書いたものだけを始まった順に()
    {
        var threads = Detail().Threads;
        Assert.Equal(2, threads.Count);   // システムの記録と消したスレッドは除く
        Assert.Null(threads[0].FilePath);
        Assert.Equal("fixed", threads[0].Status);
        Assert.Equal("/src/a.cs", threads[1].FilePath);
        Assert.Equal(42, threads[1].Line);
        Assert.Equal(new[] { "佐藤", "山田" }, threads[1].Comments.Select(c => c.Author));
    }

    [Fact]
    public void 写しにレビュアーの投票と行に付いた会話が並ぶ()
    {
        var markdown = AzureDevOpsPullRequestJson.Markdown(Detail());

        Assert.StartsWith("# PR 12 機能 X", markdown);
        Assert.Contains("| マージ | 競合あり |", markdown);
        Assert.Contains("| Work Item | AB#345 |", markdown);
        Assert.Contains("- 佐藤（必須） — ✓ 承認", markdown);
        Assert.Contains("- 鈴木 — … 作成者の対応待ち", markdown);
        Assert.Contains("### `src/a.cs:42` — 未解決", markdown);
        Assert.Contains("### 全体 — 解決済み", markdown);
        Assert.DoesNotContain("voted", markdown);
    }

    [Fact]
    public void 欄が欠けていても読める()
    {
        using var doc = JsonDocument.Parse("""{"pullRequestId":5}""");
        var pull = AzureDevOpsPullRequestJson.ParsePullRequest(doc.RootElement, Org, Target);
        Assert.Equal(5, pull.Id);
        Assert.Equal("", pull.SourceBranch);
        Assert.Null(pull.CreatedAt);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/Proj/_git/Repo", true)]
    [InlineData("https://user@dev.azure.com/org/Proj/_git/Repo", true)]
    [InlineData("https://org.visualstudio.com/DefaultCollection/Proj/_git/Repo", true)]
    [InlineData("https://tfs.example.local/tfs/Collection/Proj/_git/repo", true)]
    [InlineData("git@ssh.dev.azure.com:v3/org/Proj/Repo", true)]
    [InlineData("org@vs-ssh.visualstudio.com:v3/org/Proj/Repo", true)]
    [InlineData("https://dev.azure.com/org/Proj/_git/Other", false)]
    [InlineData("https://dev.azure.com/org/OtherProj/_git/Repo", false)]
    [InlineData("https://github.com/org/Repo.git", false)]
    [InlineData("", false)]
    public void PRのリポジトリを指すリモートを見分ける(string url, bool matches)
        => Assert.Equal(matches, AzureDevOpsPullRequestJson.RemoteMatches(url, "Proj", "Repo"));

    [Theory]
    [InlineData("https://dev.azure.com/org/My%20Project/_git/My%20Project", "My Project", "My Project", true)]
    [InlineData("https://dev.azure.com/org/_git/Same", "Same", "Same", true)]
    [InlineData("https://dev.azure.com/org/_git/Repo", "Proj", "Repo", false)]
    public void 空白と省いたプロジェクトも読む(string url, string project, string repository, bool matches)
        => Assert.Equal(matches, AzureDevOpsPullRequestJson.RemoteMatches(url, project, repository));
}
