using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブランチ一覧ツリーのノード。<see cref="Branch"/> が null のノードはフォルダ
/// （"feature/x" の "feature"）か、ローカル／リモートを分ける見出し（<see cref="IsSection"/>）。
/// Label は自分のセグメントのみで、フルネームはリーフの <see cref="Branch"/>.Name が持つ。
/// </summary>
public sealed class BranchTreeNode
{
    public required string Label { get; init; }
    public GitBranchInfo? Branch { get; init; }
    public List<BranchTreeNode> Children { get; } = new();

    /// <summary>ローカル／リモート（origin 等）を分けるトップレベルの見出しか。
    /// フォルダとしては同じ（開閉する）が、見た目と意味が違うのでビューが区別する。</summary>
    public bool IsSection { get; init; }

    public bool IsFolder => Branch is null;
    public bool IsCurrent => Branch?.IsCurrent == true;
    public bool IsRemote => Branch?.IsRemote == true;

    /// <summary>TreeView の展開状態（ItemContainerStyle が TwoWay でバインドする）。
    /// 既定は折りたたみで、現在ブランチへの経路上のフォルダだけ Build が展開する。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>リーフのツールチップ：フルネーム＋上流（あれば）。フォルダ・見出しには出さない。</summary>
    public string? ToolTip => Branch is null ? null
        : string.IsNullOrEmpty(Branch.Upstream) ? Branch.Name : $"{Branch.Name} → {Branch.Upstream}";

    /// <summary>配下のブランチ数（見出しの件数ピル用）。リーフは自分自身で 1。</summary>
    public int LeafCount => Branch is not null ? 1 : Children.Sum(c => c.LeafCount);

    /// <summary>上流との差（例: "↑2 ↓1"）。差が無い・上流が無いなら空。</summary>
    public string TrackLabel => Branch is null ? "" : (Branch.Ahead, Branch.Behind) switch
    {
        (0, 0) => "",
        (var a, 0) => $"↑{a}",
        (0, var b) => $"↓{b}",
        var (a, b) => $"↑{a} ↓{b}",
    };

    /// <summary>上流が消えている（リモートで削除された）。整理の目印としてビューが出す。</summary>
    public bool IsUpstreamGone => Branch?.UpstreamGone == true;

    /// <summary>先頭コミットの相対日時（例: "2日前"）。日時を持たないノードは空。</summary>
    public string RelativeDate => Branch?.LastCommit is { } at ? FormatRelative(at) : "";

    /// <summary>「いつ触ったブランチか」が分かれば十分なので、粒度は粗くてよい。</summary>
    private static string FormatRelative(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        if (span < TimeSpan.Zero) return "";               // 時計ずれ。出さない
        if (span.TotalMinutes < 1) return "たった今";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}時間前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}日前";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}か月前";
        return $"{(int)(span.TotalDays / 365)}年前";
    }
}

/// <summary>
/// ブランチ一覧を、ローカル／リモート（origin 等）の見出しで分け、その中を "/" 区切りで
/// フォルダ化したツリーへ変換する。見出しはリモートブランチが1つでもあるときだけ付ける
/// （分ける相手がいないのに階層を挟むとクリックが増えるだけのため）。同じ理由で、見出しの中の
/// ブランチが <see cref="MinBranchesForTree"/> 個未満ならフォルダ化もしない。
/// </summary>
public static class BranchTreeBuilder
{
    public const int MinBranchesForTree = 3;

    /// <summary>ローカルブランチの見出しラベル。リモート側の見出しはリモート名そのもの。</summary>
    public const string LocalSectionLabel = "ローカル";

    /// <summary>
    /// リフレッシュ用：ブランチ構成が <paramref name="current"/> と同じならインスタンスをそのまま返す
    /// （ビューの開閉・選択・スクロールを壊さない。RepositoryChanged はファイル編集でも頻発するため）。
    /// 変わったときだけ作り直し、ユーザーが操作した開閉状態をラベルパスで引き継ぐ。
    /// </summary>
    public static IReadOnlyList<BranchTreeNode> Update(
        IReadOnlyList<BranchTreeNode> current, IReadOnlyList<GitBranchInfo> branches)
    {
        if (Flatten(current).SequenceEqual(branches))
            return current;

        var tree = Build(branches);
        CarryExpansion(current, tree);
        return tree;
    }

    private static IEnumerable<GitBranchInfo> Flatten(IReadOnlyList<BranchTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Branch is { } branch)
                yield return branch;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    /// <summary>旧ツリーのフォルダ開閉状態を同じラベルパスの新フォルダへ移す。
    /// ただし現在ブランチへの経路（Build が展開済み）は畳まない（チェックアウト直後に見えるように）。</summary>
    private static void CarryExpansion(
        IReadOnlyList<BranchTreeNode> oldNodes, IReadOnlyList<BranchTreeNode> newNodes)
    {
        foreach (var newFolder in newNodes.Where(n => n.IsFolder))
        {
            var oldFolder = oldNodes.FirstOrDefault(n => n.IsFolder && n.Label == newFolder.Label);
            if (oldFolder is null)
                continue;
            newFolder.IsExpanded |= oldFolder.IsExpanded;
            CarryExpansion(oldFolder.Children, newFolder.Children);
        }
    }

    /// <summary>
    /// 絞り込み表示用：一致するブランチだけをフルネームの平坦な行として返す。見出しもフォルダも挟まない
    /// ——絞り込み中に知りたいのは「どこにあるか」ではなく「何が一致したか」で、階層はクリックを増やす
    /// だけになる。空語なら通常の <see cref="Build"/> と同じ。
    /// </summary>
    public static IReadOnlyList<BranchTreeNode> BuildFiltered(
        IReadOnlyList<GitBranchInfo> branches, string? term)
    {
        var t = term?.Trim();
        if (string.IsNullOrEmpty(t))
            return Build(branches);

        return branches
            .Where(b => b.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Select(b => new BranchTreeNode { Label = b.Name, Branch = b })
            .ToList();
    }

    public static IReadOnlyList<BranchTreeNode> Build(IReadOnlyList<GitBranchInfo> branches)
    {
        var locals = branches.Where(b => !b.IsRemote).ToList();
        var remotes = branches.Where(b => b.IsRemote).ToList();

        // リモートが無ければ分ける相手がいない。見出しを足さず、そのままフォルダ化する
        if (remotes.Count == 0)
            return BuildGroup(locals, stripSegments: 0);

        var roots = new List<BranchTreeNode>();
        if (locals.Count > 0)
        {
            // ローカルは常に開いておく（現在ブランチが見えないと切替が始まらない）
            var section = new BranchTreeNode
            {
                Label = LocalSectionLabel,
                IsSection = true,
                IsExpanded = true,
            };
            section.Children.AddRange(BuildGroup(locals, stripSegments: 0));
            roots.Add(section);
        }

        // リモートは既定で畳む（origin/* はローカルの倍あることが多く、開いていると一覧が埋まる）。
        // 見出し配下ではリモート名の1セグメントを剥がす（"origin" の中に "origin/" は要らない）。
        foreach (var group in remotes.GroupBy(b => b.Name.Split('/')[0]))
        {
            var section = new BranchTreeNode { Label = group.Key, IsSection = true };
            section.Children.AddRange(BuildGroup(group.ToList(), stripSegments: 1));
            roots.Add(section);
        }
        return roots;
    }

    /// <summary>
    /// 1つの見出し配下（またはリモートなしのときの全体）を組む。<paramref name="stripSegments"/> は
    /// ラベルから落とす先頭セグメント数（リモート見出しなら 1＝リモート名ぶん）。
    /// </summary>
    private static List<BranchTreeNode> BuildGroup(
        IReadOnlyList<GitBranchInfo> branches, int stripSegments)
    {
        var roots = new List<BranchTreeNode>();
        var folderize = branches.Count >= MinBranchesForTree;

        // 入力順（git branch -a の refname ソート）を保ち、フォルダは初出位置に置く
        foreach (var branch in branches)
        {
            var segments = branch.Name.Split('/')[stripSegments..];
            if (!folderize)
            {
                roots.Add(new BranchTreeNode { Label = string.Join('/', segments), Branch = branch });
                continue;
            }

            var children = roots;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var label = segments[i];
                // 同名のリーフ（"origin" というローカルブランチ等）とは別物としてフォルダだけを探す
                var folder = children.FirstOrDefault(n => n.IsFolder && n.Label == label);
                if (folder is null)
                {
                    folder = new BranchTreeNode { Label = label };
                    children.Add(folder);
                }
                // 現在ブランチが埋もれて見えなくならないよう、その経路上だけ既定で展開する
                folder.IsExpanded |= branch.IsCurrent;
                children = folder.Children;
            }
            children.Add(new BranchTreeNode { Label = segments[^1], Branch = branch });
        }
        return roots;
    }
}
