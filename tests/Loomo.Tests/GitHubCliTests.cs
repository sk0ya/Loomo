using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.Tests;

/// <summary>GitHub の PR／Issue（§24.27）。gh の <c>--json</c> 出力の読み方と、エディタで読む写しの組み立て（純粋）。
/// 入力は gh 2.86 の実出力と同じ形。</summary>
public class GitHubCliTests
{
    private const string PullRequestList = """
        [{"author":{"id":"U_1","is_bot":false,"login":"alice","name":"Alice"},"baseRefName":"main",
          "headRefName":"feature/x","isCrossRepository":true,"isDraft":false,"number":42,
          "reviewDecision":"REVIEW_REQUIRED","title":"Add x","updatedAt":"2026-10-17T15:22:13Z",
          "url":"https://github.com/o/r/pull/42",
          "statusCheckRollup":[
            {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://ci/1"},
            {"__typename":"CheckRun","name":"label","status":"COMPLETED","conclusion":"SKIPPED"},
            {"__typename":"StatusContext","context":"legacy","state":"PENDING","targetUrl":"https://ci/2"}]},
         {"author":{"login":"bob"},"baseRefName":"main","headRefName":"wip","isCrossRepository":false,"isDraft":true,
          "number":7,"reviewDecision":"","title":"WIP","updatedAt":"2026-10-01T00:00:00Z","url":"u","statusCheckRollup":[]}]
        """;

    [Fact]
    public void PRの一覧を読む()
    {
        var pulls = GitHubJson.ParsePullRequests(PullRequestList);

        Assert.Equal(2, pulls.Count);
        var pr = pulls[0];
        Assert.Equal(42, pr.Number);
        Assert.Equal("alice", pr.Author);
        Assert.Equal("feature/x", pr.HeadRef);
        Assert.Equal("main", pr.BaseRef);
        Assert.True(pr.IsCrossRepository);
        Assert.Equal(GitHubChecksState.Pending, pr.Checks);   // StatusContext が待ち
        Assert.True(pulls[1].IsDraft);
        Assert.Equal(GitHubChecksState.None, pulls[1].Checks);
    }

    [Theory]
    [InlineData(new[] { GitHubChecksState.Success, GitHubChecksState.Failure, GitHubChecksState.Pending }, GitHubChecksState.Failure)]
    [InlineData(new[] { GitHubChecksState.Success, GitHubChecksState.Pending }, GitHubChecksState.Pending)]
    [InlineData(new[] { GitHubChecksState.Success }, GitHubChecksState.Success)]
    [InlineData(new GitHubChecksState[0], GitHubChecksState.None)]
    public void チェックのまとめは失敗_待ち_成功の順に強い(GitHubChecksState[] checks, GitHubChecksState expected)
        => Assert.Equal(expected, GitHubJson.Summarize(checks));

    [Fact]
    public void 失敗と取り消しと未完了を読み分ける()
    {
        var pulls = GitHubJson.ParsePullRequests("""
            [{"number":1,"statusCheckRollup":[{"__typename":"CheckRun","name":"a","status":"COMPLETED","conclusion":"CANCELLED"}]},
             {"number":2,"statusCheckRollup":[{"__typename":"CheckRun","name":"a","status":"IN_PROGRESS","conclusion":""}]},
             {"number":3,"statusCheckRollup":[{"__typename":"StatusContext","context":"a","state":"ERROR"}]}]
            """);
        Assert.Equal(new[] { GitHubChecksState.Failure, GitHubChecksState.Pending, GitHubChecksState.Failure },
            pulls.Select(p => p.Checks));
    }

    [Fact]
    public void PRの中身はコメントとレビューを時刻順の会話にする()
    {
        var detail = GitHubJson.ParsePullRequestDetail("""
            {"number":42,"title":"Add x","author":{"login":"alice"},"headRefName":"feature/x","baseRefName":"main",
             "body":"本文","state":"OPEN","additions":12,"deletions":3,"headRefOid":"abc123","mergeable":"MERGEABLE",
             "files":[{"path":"src/a.cs","additions":12,"deletions":3}],
             "comments":[{"author":{"login":"bot"},"body":"thanks","createdAt":"2026-10-17T15:00:00Z"}],
             "reviews":[
               {"author":{"login":"carol"},"body":"直して","state":"CHANGES_REQUESTED","submittedAt":"2026-10-17T16:00:00Z"},
               {"author":{"login":"dave"},"body":"","state":"COMMENTED","submittedAt":"2026-10-17T14:00:00Z"},
               {"author":{"login":"erin"},"body":"","state":"APPROVED","submittedAt":"2026-10-17T17:00:00Z"}],
             "statusCheckRollup":[]}
            """);

        Assert.Equal("abc123", detail.HeadOid);
        Assert.Single(detail.Files);
        // 本文の無い COMMENTED（行コメントの入れ物）は出さない。本文の無い承認は出す。
        Assert.Equal(new[] { "bot", "carol", "erin" }, detail.Conversation.Select(c => c.Author));
        Assert.Equal("CHANGES_REQUESTED", detail.Conversation[1].State);
    }

    [Fact]
    public void 欄が欠けていても読める()
    {
        var pulls = GitHubJson.ParsePullRequests("""[{"number":5}]""");
        Assert.Equal(5, pulls[0].Number);
        Assert.Equal("", pulls[0].Title);
        Assert.Equal("", pulls[0].Author);
        Assert.Null(pulls[0].UpdatedAt);
    }

    [Fact]
    public void Issueの一覧と中身を読む()
    {
        var issues = GitHubJson.ParseIssues("""
            [{"number":9,"title":"Bug","author":{"login":"zoe"},"labels":[{"name":"bug"},{"name":"p1"}],
              "updatedAt":"2026-10-10T00:00:00Z","url":"https://github.com/o/r/issues/9",
              "comments":[{"author":{"login":"a"},"body":"x","createdAt":"2026-10-11T00:00:00Z"}],"state":"OPEN"}]
            """);
        Assert.Equal(new[] { "bug", "p1" }, issues[0].Labels);
        Assert.Equal(1, issues[0].CommentCount);

        var detail = GitHubJson.ParseIssueDetail("""
            {"number":9,"title":"Bug","author":{"login":"zoe"},"labels":[],"url":"u","state":"OPEN","body":"再現手順",
             "comments":[{"author":{"login":"b"},"body":"2","createdAt":"2026-10-12T00:00:00Z"},
                         {"author":{"login":"a"},"body":"1","createdAt":"2026-10-11T00:00:00Z"}]}
            """);
        Assert.Equal(new[] { "a", "b" }, detail.Comments.Select(c => c.Author));
    }

    [Fact]
    public void PRの写しに状態_ファイル_会話が並ぶ()
    {
        var detail = GitHubJson.ParsePullRequestDetail("""
            {"number":42,"title":"Add x","author":{"login":"alice"},"headRefName":"feature/x","baseRefName":"main",
             "body":"","state":"OPEN","isDraft":true,"additions":1,"deletions":0,"mergeable":"CONFLICTING",
             "files":[{"path":"a|b.cs","additions":1,"deletions":0}],"comments":[],"reviews":[],
             "statusCheckRollup":[{"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://ci/1"}]}
            """);

        var markdown = GitHubMarkdown.PullRequest(detail);

        Assert.StartsWith("# #42 Add x", markdown);
        Assert.Contains("| 状態 | 下書き |", markdown);
        Assert.Contains("| マージ | 競合あり |", markdown);
        Assert.Contains("_（本文なし）_", markdown);
        Assert.Contains("- ✗ [build](https://ci/1)", markdown);
        Assert.Contains("- `a|b.cs` +1 −0", markdown);
        Assert.Contains("## 会話（0 件）", markdown);
    }

    [Fact]
    public void 一覧の右端は下書き_チェック_レビューを短く()
    {
        var pr = GitHubJson.ParsePullRequests("""
            [{"number":1,"isDraft":true,"reviewDecision":"APPROVED",
              "statusCheckRollup":[{"__typename":"CheckRun","name":"a","status":"COMPLETED","conclusion":"SUCCESS"}]}]
            """)[0];
        Assert.Equal("下書き ✓ 承認", GitHubMarkdown.Badge(pr));
    }
}
