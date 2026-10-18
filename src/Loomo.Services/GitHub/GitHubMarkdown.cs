using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Services.GitHub;

/// <summary>
/// PR・Issue を、エディタで読む Markdown の写しにする（純粋）。部屋のエディタ／Markdown プレビューでそのまま
/// 読めて、ペグボードやコンポーザへ素材として渡せる。書き戻しはしない（コメントは別の操作で送る）。
/// </summary>
public static class GitHubMarkdown
{
    public static string PullRequest(GitHubPullRequestDetail detail)
    {
        var pr = detail.Summary;
        var text = new StringBuilder();
        text.Append("# #").Append(pr.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').AppendLine(pr.Title).AppendLine();
        text.AppendLine("| | |").AppendLine("|---|---|");
        Row(text, "状態", StateLabel(detail.State, pr.IsDraft));
        Row(text, "作成者", $"@{pr.Author}");
        Row(text, "ブランチ", $"`{pr.HeadRef}` → `{pr.BaseRef}`{(pr.IsCrossRepository ? "（フォークから）" : "")}");
        Row(text, "変更", $"+{detail.Additions} −{detail.Deletions}（{detail.Files.Count} ファイル）");
        if (ReviewLabel(pr.ReviewDecision) is { } review) Row(text, "レビュー", review);
        Row(text, "チェック", ChecksLabel(pr.Checks));
        if (MergeableLabel(detail.Mergeable) is { } mergeable) Row(text, "マージ", mergeable);
        Row(text, "URL", pr.Url);
        text.AppendLine();

        Section(text, "概要", detail.Body);

        if (detail.CheckRuns.Count > 0)
        {
            text.AppendLine("## チェック").AppendLine();
            foreach (var check in detail.CheckRuns.OrderBy(c => c.State != GitHubChecksState.Failure).ThenBy(c => c.Name, StringComparer.Ordinal))
                text.Append("- ").Append(CheckMark(check.State)).Append(' ')
                    .AppendLine(check.Url is { } url ? $"[{check.Name}]({url})" : check.Name);
            text.AppendLine();
        }

        if (detail.Files.Count > 0)
        {
            text.AppendLine("## 変わったファイル").AppendLine();
            foreach (var file in detail.Files)
                text.Append("- `").Append(file.Path).Append("` +").Append(file.Additions).Append(" −").AppendLine(file.Deletions.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
        }

        Conversation(text, detail.Conversation);
        return text.ToString();
    }

    public static string Issue(GitHubIssueDetail detail)
    {
        var issue = detail.Summary;
        var text = new StringBuilder();
        text.Append("# #").Append(issue.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').AppendLine(issue.Title).AppendLine();
        text.AppendLine("| | |").AppendLine("|---|---|");
        Row(text, "状態", StateLabel(issue.State, isDraft: false));
        Row(text, "作成者", $"@{issue.Author}");
        if (issue.Labels.Count > 0) Row(text, "ラベル", string.Join(" ", issue.Labels.Select(l => $"`{l}`")));
        Row(text, "URL", issue.Url);
        text.AppendLine();
        Section(text, "本文", detail.Body);
        Conversation(text, detail.Comments);
        return text.ToString();
    }

    public static string ChecksLabel(GitHubChecksState state) => state switch
    {
        GitHubChecksState.Success => "✓ 通過",
        GitHubChecksState.Failure => "✗ 失敗あり",
        GitHubChecksState.Pending => "… 実行中",
        _ => "なし",
    };

    public static string? ReviewLabel(string decision) => decision switch
    {
        "APPROVED" => "承認済み",
        "CHANGES_REQUESTED" => "変更を求められている",
        "REVIEW_REQUIRED" => "レビュー待ち",
        _ => null,
    };

    /// <summary>一覧の右端に添える短い状態（下書き・チェック・レビュー）。</summary>
    public static string Badge(GitHubPullRequest pr)
    {
        var parts = new List<string>();
        if (pr.IsDraft) parts.Add("下書き");
        if (pr.Checks is not GitHubChecksState.None) parts.Add(CheckMark(pr.Checks));
        if (pr.ReviewDecision == "APPROVED") parts.Add("承認");
        else if (pr.ReviewDecision == "CHANGES_REQUESTED") parts.Add("要修正");
        return string.Join(" ", parts);
    }

    private static string CheckMark(GitHubChecksState state) => state switch
    {
        GitHubChecksState.Success => "✓",
        GitHubChecksState.Failure => "✗",
        GitHubChecksState.Pending => "…",
        _ => "",
    };

    private static string StateLabel(string state, bool isDraft) => (state.ToUpperInvariant(), isDraft) switch
    {
        ("OPEN", true) => "下書き",
        ("OPEN", false) => "オープン",
        ("MERGED", _) => "マージ済み",
        ("CLOSED", _) => "クローズ",
        _ => state,
    };

    private static string? MergeableLabel(string mergeable) => mergeable switch
    {
        "MERGEABLE" => "競合なし",
        "CONFLICTING" => "競合あり",
        _ => null,
    };

    private static string? ReviewStateLabel(string? state) => state switch
    {
        "APPROVED" => "✓ 承認",
        "CHANGES_REQUESTED" => "✗ 変更を求めた",
        "COMMENTED" => "レビューコメント",
        "DISMISSED" => "取り下げられたレビュー",
        _ => null,
    };

    private static void Row(StringBuilder text, string name, string value)
        => text.Append("| ").Append(name).Append(" | ").Append(value.Replace("|", "\\|")).AppendLine(" |");

    private static void Section(StringBuilder text, string heading, string body)
    {
        text.Append("## ").AppendLine(heading).AppendLine();
        text.AppendLine(body.Trim().Length == 0 ? "_（本文なし）_" : body.Trim().Replace("\r\n", "\n")).AppendLine();
    }

    private static void Conversation(StringBuilder text, IReadOnlyList<GitHubComment> comments)
    {
        text.Append("## 会話（").Append(comments.Count).AppendLine(" 件）").AppendLine();
        if (comments.Count == 0)
        {
            text.AppendLine("_（まだありません）_");
            return;
        }
        foreach (var comment in comments)
        {
            text.Append("### @").Append(comment.Author);
            if (ReviewStateLabel(comment.State) is { } label) text.Append(" — ").Append(label);
            if (comment.CreatedAt is { } at) text.Append(" · ").Append(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            text.AppendLine().AppendLine();
            if (comment.Body.Trim().Length > 0)
                text.AppendLine(comment.Body.Trim().Replace("\r\n", "\n")).AppendLine();
        }
    }
}
