using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.Services;

/// <summary>レビュアー1人と投票（10 承認・5 提案付きで承認・0 未投票・-5 作成者待ち・-10 却下）。</summary>
public sealed record AzureDevOpsReviewer(string Name, int Vote, bool IsRequired);

/// <summary>会話の1件。</summary>
public sealed record AzureDevOpsComment(string Author, string Content, DateTimeOffset? PublishedAt);

/// <summary>スレッド1つ。<see cref="FilePath"/> があればコードの行に付いたもの。</summary>
public sealed record AzureDevOpsThread(
    string? FilePath, int? Line, string Status, IReadOnlyList<AzureDevOpsComment> Comments)
{
    public DateTimeOffset? StartedAt => Comments.Count > 0 ? Comments[0].PublishedAt : null;
}

/// <summary>PR の中身（§24.27.1）。</summary>
public sealed record AzureDevOpsPullRequestDetail(
    AzureDevOpsPullRequest Summary, string Description, string Status, string MergeStatus,
    IReadOnlyList<AzureDevOpsReviewer> Reviewers, IReadOnlyList<AzureDevOpsThread> Threads);

/// <summary>
/// Azure DevOps の PR の応答を読む・写しにする（純粋）。欄が欠けていても既定値で読む。
/// </summary>
public static class AzureDevOpsPullRequestJson
{
    public static AzureDevOpsPullRequest ParsePullRequest(JsonElement item, string organizationUrl, AzureDevOpsPrTarget target)
    {
        var id = item.TryGetProperty("pullRequestId", out var idProp) && idProp.TryGetInt32(out var n) ? n : 0;
        var linkedIds = new List<int>();
        if (item.TryGetProperty("workItemRefs", out var refs) && refs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in refs.EnumerateArray())
            {
                if (r.TryGetProperty("id", out var widProp)
                    && widProp.ValueKind == JsonValueKind.String
                    && int.TryParse(widProp.GetString(), out var wid))
                    linkedIds.Add(wid);
            }
        }
        return new AzureDevOpsPullRequest(
            id,
            Str(item, "title"),
            target.Project,
            target.Repository,
            item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            $"{organizationUrl}/{Uri.EscapeDataString(target.Project)}/_git/{Uri.EscapeDataString(target.Repository)}/pullrequest/{id}",
            linkedIds,
            Name(item, "createdBy"),
            Str(item, "sourceRefName"),
            Str(item, "targetRefName"),
            Date(item, "creationDate"));
    }

    public static AzureDevOpsPullRequestDetail ParseDetail(
        JsonElement pr, JsonElement threads, string organizationUrl, AzureDevOpsPrTarget target)
    {
        var reviewers = Array(pr, "reviewers")
            .Select(r => new AzureDevOpsReviewer(
                Str(r, "displayName"),
                r.TryGetProperty("vote", out var vote) && vote.TryGetInt32(out var v) ? v : 0,
                r.TryGetProperty("isRequired", out var required) && required.ValueKind == JsonValueKind.True))
            .Where(r => r.Name.Length > 0)
            .ToList();
        return new AzureDevOpsPullRequestDetail(
            ParsePullRequest(pr, organizationUrl, target), Str(pr, "description"), Str(pr, "status"),
            Str(pr, "mergeStatus"), reviewers, ParseThreads(threads));
    }

    /// <summary>人の書いた会話だけ（投票・更新などのシステムの記録と、消されたものは除く）を、始まった順に。</summary>
    public static IReadOnlyList<AzureDevOpsThread> ParseThreads(JsonElement root)
    {
        var result = new List<AzureDevOpsThread>();
        foreach (var thread in Array(root, "value"))
        {
            if (Bool(thread, "isDeleted")) continue;
            var comments = Array(thread, "comments")
                .Where(c => !Bool(c, "isDeleted") && Str(c, "commentType") != "system" && Str(c, "content").Trim().Length > 0)
                .Select(c => new AzureDevOpsComment(Name(c, "author"), Str(c, "content"), Date(c, "publishedDate")))
                .ToList();
            if (comments.Count == 0) continue;
            string? path = null;
            int? line = null;
            if (thread.TryGetProperty("threadContext", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                path = Str(context, "filePath") is { Length: > 0 } p ? p : null;
                foreach (var side in new[] { "rightFileStart", "leftFileStart" })
                {
                    if (context.TryGetProperty(side, out var start) && start.ValueKind == JsonValueKind.Object &&
                        start.TryGetProperty("line", out var l) && l.TryGetInt32(out var number))
                    {
                        line = number;
                        break;
                    }
                }
            }
            result.Add(new AzureDevOpsThread(path, line, Str(thread, "status"), comments));
        }
        return result.OrderBy(t => t.StartedAt ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary><c>refs/heads/feature/x</c> → <c>feature/x</c>。</summary>
    public static string BranchName(string refName)
        => refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    /// <summary>
    /// このリモート URL がその PR のリポジトリを指しているか。クラウド（<c>dev.azure.com</c>・<c>*.visualstudio.com</c>）・
    /// Azure DevOps Server（<c>/tfs/コレクション/…</c>）の HTTPS と、SSH（<c>v3/組織/プロジェクト/リポジトリ</c>）を読む。
    /// プロジェクトを省いた形（リポジトリ名＝プロジェクト名のとき）も受ける。大文字小文字と %20 は区別しない。
    /// </summary>
    public static bool RemoteMatches(string? remoteUrl, string project, string repository)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return false;
        var url = remoteUrl.Trim();
        string path;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh")
            path = uri.AbsolutePath;
        else if (url.IndexOf(':') is var colon and > 0 && !url.Contains("://", StringComparison.Ordinal))
            path = url[(colon + 1)..];   // git@ssh.dev.azure.com:v3/org/project/repo
        else
            return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Uri.UnescapeDataString(s)).ToList();
        if (segments.Count > 0 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            segments[^1] = segments[^1][..^4];

        string? repo, proj;
        var git = segments.FindIndex(s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (git >= 0 && git + 1 < segments.Count)
        {
            repo = segments[git + 1];
            proj = git >= 1 ? segments[git - 1] : null;
        }
        else if (segments.Count >= 4 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
        {
            repo = segments[^1];
            proj = segments[^2];
        }
        else
        {
            return false;
        }

        if (!repo.Equals(repository, StringComparison.OrdinalIgnoreCase)) return false;
        // プロジェクトを省いた形では、手前の段は組織（やコレクション）——リポジトリ名がプロジェクト名と同じときだけ省ける。
        return proj is null || proj.Equals(project, StringComparison.OrdinalIgnoreCase) ||
               repository.Equals(project, StringComparison.OrdinalIgnoreCase);
    }

    public static string VoteLabel(int vote) => vote switch
    {
        >= 10 => "✓ 承認",
        >= 5 => "✓ 提案付きで承認",
        <= -10 => "✗ 却下",
        <= -5 => "… 作成者の対応待ち",
        _ => "未投票",
    };

    /// <summary>エディタで読む Markdown の写し。保存しても Azure DevOps へは戻さない。</summary>
    public static string Markdown(AzureDevOpsPullRequestDetail detail)
    {
        var pr = detail.Summary;
        var text = new StringBuilder();
        text.Append("# PR ").Append(pr.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').AppendLine(pr.Title).AppendLine();
        text.AppendLine("| | |").AppendLine("|---|---|");
        Row(text, "状態", StatusLabel(detail.Status, pr.IsDraft));
        Row(text, "作成者", pr.Author);
        Row(text, "ブランチ", $"`{pr.SourceBranch}` → `{pr.TargetBranch}`");
        Row(text, "リポジトリ", $"{pr.Project} / {pr.Repository}");
        if (MergeLabel(detail.MergeStatus) is { } merge) Row(text, "マージ", merge);
        if (pr.LinkedWorkItemIds.Count > 0)
            Row(text, "Work Item", string.Join(" ", pr.LinkedWorkItemIds.Select(id => $"AB#{id}")));
        Row(text, "URL", pr.WebUrl);
        text.AppendLine();

        if (detail.Reviewers.Count > 0)
        {
            text.AppendLine("## レビュアー").AppendLine();
            foreach (var reviewer in detail.Reviewers.OrderByDescending(r => r.IsRequired).ThenBy(r => r.Name, StringComparer.CurrentCulture))
                text.Append("- ").Append(reviewer.Name).Append(reviewer.IsRequired ? "（必須）" : "")
                    .Append(" — ").AppendLine(VoteLabel(reviewer.Vote));
            text.AppendLine();
        }

        text.AppendLine("## 説明").AppendLine();
        text.AppendLine(detail.Description.Trim().Length == 0 ? "_（説明なし）_" : detail.Description.Trim().Replace("\r\n", "\n")).AppendLine();

        text.Append("## 会話（").Append(detail.Threads.Count).AppendLine(" スレッド）").AppendLine();
        if (detail.Threads.Count == 0)
            text.AppendLine("_（まだありません）_");
        foreach (var thread in detail.Threads)
        {
            text.Append("### ");
            text.Append(thread.FilePath is { } file ? $"`{file.TrimStart('/')}{(thread.Line is { } line ? $":{line}" : "")}`" : "全体");
            if (ThreadStatusLabel(thread.Status) is { } status) text.Append(" — ").Append(status);
            text.AppendLine().AppendLine();
            foreach (var comment in thread.Comments)
            {
                text.Append("**").Append(comment.Author).Append("**");
                if (comment.PublishedAt is { } at)
                    text.Append(" · ").Append(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                text.AppendLine().AppendLine();
                text.AppendLine(comment.Content.Trim().Replace("\r\n", "\n")).AppendLine();
            }
        }
        return text.ToString();
    }

    private static string StatusLabel(string status, bool isDraft) => (status.ToLowerInvariant(), isDraft) switch
    {
        ("active", true) => "下書き",
        ("active", false) => "進行中",
        ("completed", _) => "完了",
        ("abandoned", _) => "破棄",
        _ => status,
    };

    private static string? MergeLabel(string mergeStatus) => mergeStatus switch
    {
        "succeeded" => "競合なし",
        "conflicts" => "競合あり",
        "queued" => "確認中",
        "rejectedByPolicy" => "ポリシーで拒否",
        "failure" => "マージ確認に失敗",
        _ => null,
    };

    private static string? ThreadStatusLabel(string status) => status switch
    {
        "active" or "pending" => "未解決",
        "fixed" => "解決済み",
        "wontFix" => "対応しない",
        "closed" => "クローズ",
        "byDesign" => "仕様どおり",
        _ => null,
    };

    private static void Row(StringBuilder text, string name, string value)
        => text.Append("| ").Append(name).Append(" | ").Append(value.Replace("|", "\\|")).AppendLine(" |");

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Name(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var person) && person.ValueKind == JsonValueKind.Object
            ? Str(person, "displayName")
            : "";

    private static DateTimeOffset? Date(JsonElement e, string name)
        => DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;
}
