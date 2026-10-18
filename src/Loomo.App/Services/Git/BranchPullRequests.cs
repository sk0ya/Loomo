using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブランチ一覧の行に、そのブランチから出ている PR を添える（§24.39・lazygit v0.61.0 の「Show pull requests against branches」）。
/// どのブランチがレビュー待ちで、どれが承認済みかを一覧のまま見られる。PR の中身と操作は §24.27 の面へつなぐ。
/// 照合はブランチ名（PR の head）だけで行う。リモートのブランチ（<c>origin/feature</c>）はリモート名を外して比べる。
/// フォークから来た PR は head の名前が手元のブランチと偶然同じでも別物なので添えない。
/// </summary>
public static class BranchPullRequests
{
    /// <summary>head のブランチ名 → PR。同じ head の PR が複数あれば番号の大きい（新しい）方。</summary>
    public static IReadOnlyDictionary<string, GitHubPullRequest> ByHead(IEnumerable<GitHubPullRequest> pulls)
        => pulls
            .Where(pr => !pr.IsCrossRepository && !string.IsNullOrWhiteSpace(pr.HeadRef))
            .GroupBy(pr => pr.HeadRef, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(pr => pr.Number).First(), StringComparer.Ordinal);

    /// <summary>ツリーのリーフに PR を付け直す（無ければ外す）。</summary>
    public static void Apply(IEnumerable<BranchTreeNode>? roots, IReadOnlyDictionary<string, GitHubPullRequest> byHead)
    {
        if (roots is null) return;
        var stack = new Stack<BranchTreeNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Branch is { } branch)
                node.PullRequest = byHead.GetValueOrDefault(HeadName(branch.Name, branch.IsRemote));
            foreach (var child in node.Children)
                stack.Push(child);
        }
    }

    /// <summary>PR の head と比べる名前。リモートのブランチは先頭のリモート名（<c>origin/</c>）を外す。</summary>
    internal static string HeadName(string branchName, bool isRemote)
    {
        if (!isRemote) return branchName;
        var slash = branchName.IndexOf('/');
        return slash > 0 ? branchName[(slash + 1)..] : branchName;
    }

    /// <summary>行に出す短い状態。</summary>
    public static string StateLabel(GitHubPullRequest pr)
        => pr.IsDraft ? "下書き"
            : pr.ReviewDecision switch
            {
                "APPROVED" => "承認済み",
                "CHANGES_REQUESTED" => "変更を求められている",
                "REVIEW_REQUIRED" => "レビュー待ち",
                _ => "オープン",
            };
}
