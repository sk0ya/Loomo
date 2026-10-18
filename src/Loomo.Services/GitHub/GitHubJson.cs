using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Services.GitHub;

/// <summary>
/// <c>gh … --json</c> の出力を読む（純粋）。欄が無い・型が違うときは既定値にする——gh の版で欄が増減しても
/// 一覧ごと読めなくなるより、欠けた欄が空のほうがよい。
/// </summary>
public static class GitHubJson
{
    /// <summary><c>gh pr list</c> に渡す欄。</summary>
    public const string PullRequestListFields =
        "number,title,author,headRefName,baseRefName,isDraft,updatedAt,url,reviewDecision,statusCheckRollup,isCrossRepository";

    /// <summary><c>gh pr view</c> に渡す欄。</summary>
    public const string PullRequestViewFields = PullRequestListFields +
        ",body,state,additions,deletions,headRefOid,mergeable,files,comments,reviews";

    /// <summary><c>gh issue list</c> に渡す欄。</summary>
    public const string IssueListFields = "number,title,author,labels,updatedAt,url,comments,state";

    /// <summary><c>gh issue view</c> に渡す欄。</summary>
    public const string IssueViewFields = IssueListFields + ",body";

    public static IReadOnlyList<GitHubPullRequest> ParsePullRequests(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(PullRequest).ToList()
            : [];
    }

    public static GitHubPullRequestDetail ParsePullRequestDetail(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var conversation = Comments(root, "comments")
            .Concat(Array(root, "reviews").Select(review => new GitHubComment(
                Login(review), Str(review, "body"), Date(review, "submittedAt"), Str(review, "state"))))
            // 本文の無い「コメントした」レビューは、行コメントの入れ物なので会話には出さない（gh は行コメントを返さない）。
            .Where(c => c.Body.Trim().Length > 0 || c.State is not (null or "COMMENTED"))
            .OrderBy(c => c.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        return new GitHubPullRequestDetail(
            PullRequest(root), Str(root, "body"), Str(root, "state"), Int(root, "additions"), Int(root, "deletions"),
            Str(root, "headRefOid"), Str(root, "mergeable"),
            Array(root, "files").Select(f => new GitHubChangedFile(Str(f, "path"), Int(f, "additions"), Int(f, "deletions"))).ToList(),
            conversation,
            Array(root, "statusCheckRollup").Select(Check).ToList());
    }

    public static IReadOnlyList<GitHubIssue> ParseIssues(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(Issue).ToList()
            : [];
    }

    public static GitHubIssueDetail ParseIssueDetail(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new GitHubIssueDetail(Issue(root), Str(root, "body"),
            Comments(root, "comments").OrderBy(c => c.CreatedAt ?? DateTimeOffset.MinValue).ToList());
    }

    /// <summary>チェックのまとめ：1つでも落ちていれば失敗、1つでも終わっていなければ待ち、全部通れば成功。</summary>
    public static GitHubChecksState Summarize(IEnumerable<GitHubChecksState> checks)
    {
        var list = checks.ToList();
        if (list.Count == 0) return GitHubChecksState.None;
        if (list.Contains(GitHubChecksState.Failure)) return GitHubChecksState.Failure;
        if (list.Contains(GitHubChecksState.Pending)) return GitHubChecksState.Pending;
        return GitHubChecksState.Success;
    }

    private static GitHubPullRequest PullRequest(JsonElement e) => new(
        Int(e, "number"), Str(e, "title"), Login(e), Str(e, "headRefName"), Str(e, "baseRefName"),
        Bool(e, "isDraft"), Date(e, "updatedAt"), Str(e, "url"), Str(e, "reviewDecision"),
        Summarize(Array(e, "statusCheckRollup").Select(c => Check(c).State)), Bool(e, "isCrossRepository"));

    private static GitHubIssue Issue(JsonElement e) => new(
        Int(e, "number"), Str(e, "title"), Login(e),
        Array(e, "labels").Select(l => Str(l, "name")).Where(n => n.Length > 0).ToList(),
        Date(e, "updatedAt"), Str(e, "url"),
        e.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Array ? comments.GetArrayLength() : 0,
        Str(e, "state"));

    /// <summary>CheckRun（<c>status</c>＋<c>conclusion</c>）と StatusContext（<c>state</c>）の両方を読む。</summary>
    private static GitHubCheck Check(JsonElement e)
    {
        var name = Str(e, "name") is { Length: > 0 } n ? n : Str(e, "context");
        var url = Str(e, "detailsUrl") is { Length: > 0 } d ? d : Str(e, "targetUrl");
        GitHubChecksState state;
        if (Str(e, "__typename") == "StatusContext" || (Str(e, "state").Length > 0 && Str(e, "status").Length == 0))
        {
            state = Str(e, "state").ToUpperInvariant() switch
            {
                "SUCCESS" => GitHubChecksState.Success,
                "FAILURE" or "ERROR" => GitHubChecksState.Failure,
                _ => GitHubChecksState.Pending,
            };
        }
        else if (Str(e, "status").ToUpperInvariant() != "COMPLETED")
        {
            state = GitHubChecksState.Pending;
        }
        else
        {
            state = Str(e, "conclusion").ToUpperInvariant() switch
            {
                "SUCCESS" or "NEUTRAL" or "SKIPPED" => GitHubChecksState.Success,
                "" => GitHubChecksState.Pending,
                _ => GitHubChecksState.Failure,   // FAILURE / CANCELLED / TIMED_OUT / ACTION_REQUIRED / STARTUP_FAILURE
            };
        }
        return new GitHubCheck(name, state, url.Length > 0 ? url : null);
    }

    private static IEnumerable<GitHubComment> Comments(JsonElement e, string name)
        => Array(e, name).Select(c => new GitHubComment(Login(c), Str(c, "body"), Date(c, "createdAt")));

    private static string Login(JsonElement e)
        => e.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Str(author, "login") : "";

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number) ? number : 0;

    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name)
        => DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;
}
