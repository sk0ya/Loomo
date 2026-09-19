using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>コミットに付いた参照の種類。色分けの単位。</summary>
public enum GitRefKind
{
    /// <summary>デタッチ HEAD（指している枝が無い）。</summary>
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,

    /// <summary>stash・notes など、枝でもタグでもないもの。</summary>
    Other,
}

/// <summary>コミット1件に付いた参照1つ。</summary>
/// <param name="IsHead">いま HEAD が指しているか（<c>HEAD -&gt; main</c> の main）。</param>
public sealed record GitRefLabel(GitRefKind Kind, string Name, bool IsHead);

/// <summary>
/// <c>git log --decorate=full</c> の <c>%D</c>（例:
/// <c>HEAD -&gt; refs/heads/main, refs/remotes/origin/main, refs/tags/v1.0</c>）を種類つきに割る。
///
/// <para><b>完全な ref 名で受ける</b>のが肝。短縮名（<c>origin/main</c>）だけでは
/// 「リモートの main」なのか「origin/main という名前のローカルブランチ」なのかを当てられず、
/// リモート名との最長一致で推測するしかない（<see cref="GitRemoteRef"/> が同じ問題を扱っている）。
/// 装飾が短縮名で来た場合（設定違い）は種類不明として名前だけ出す——推測して間違えるより、
/// 色が付かない方がまし。</para>
/// </summary>
public static class GitRefLabels
{
    private const string HeadArrow = "HEAD -> ";

    /// <summary>git はタグにだけ種類を前置する（<c>tag: refs/tags/v1.0</c>）。剥がさないと
    /// 完全名の判定に落ちず、タグが種類不明のまま生の綴りで出る。</summary>
    private const string TagMarker = "tag: ";
    private const string LocalPrefix = "refs/heads/";
    private const string RemotePrefix = "refs/remotes/";
    private const string TagPrefix = "refs/tags/";

    public static IReadOnlyList<GitRefLabel> Parse(string? refs)
    {
        if (string.IsNullOrWhiteSpace(refs)) return Array.Empty<GitRefLabel>();

        var labels = new List<GitRefLabel>();
        foreach (var raw in refs.Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            var isHead = false;
            if (entry.StartsWith(HeadArrow, StringComparison.Ordinal))
            {
                isHead = true;
                entry = entry[HeadArrow.Length..].Trim();
            }
            else if (entry == "HEAD")
            {
                // デタッチ HEAD。指している枝が無いので、HEAD そのものを1つの参照として出す。
                labels.Add(new GitRefLabel(GitRefKind.Head, "HEAD", IsHead: true));
                continue;
            }

            labels.Add(Classify(entry, isHead));
        }
        return labels;
    }

    private static GitRefLabel Classify(string entry, bool isHead)
    {
        if (entry.StartsWith(TagMarker, StringComparison.Ordinal))
        {
            var name = entry[TagMarker.Length..].Trim();
            return new(GitRefKind.Tag,
                name.StartsWith(TagPrefix, StringComparison.Ordinal) ? name[TagPrefix.Length..] : name,
                isHead);
        }
        if (entry.StartsWith(LocalPrefix, StringComparison.Ordinal))
            return new(GitRefKind.LocalBranch, entry[LocalPrefix.Length..], isHead);
        if (entry.StartsWith(RemotePrefix, StringComparison.Ordinal))
            return new(GitRefKind.RemoteBranch, entry[RemotePrefix.Length..], isHead);
        if (entry.StartsWith(TagPrefix, StringComparison.Ordinal))
            return new(GitRefKind.Tag, entry[TagPrefix.Length..], isHead);
        // refs/stash → "stash"、それ以外（短縮名で来たとき・grafted 等）はそのまま。
        return new(GitRefKind.Other,
            entry.StartsWith("refs/", StringComparison.Ordinal) ? entry["refs/".Length..] : entry,
            isHead);
    }
}
