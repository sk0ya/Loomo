using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブランチ一覧ツリーのノード。<see cref="Branch"/> が null のノードはフォルダ
/// （"feature/x" の "feature" やリモート名 "origin"）。Label は自分のセグメントのみで、
/// フルネームはリーフの <see cref="Branch"/>.Name が持つ。
/// </summary>
public sealed class BranchTreeNode
{
    public required string Label { get; init; }
    public GitBranchInfo? Branch { get; init; }
    public List<BranchTreeNode> Children { get; } = new();

    public bool IsFolder => Branch is null;
    public bool IsCurrent => Branch?.IsCurrent == true;
    public bool IsRemote => Branch?.IsRemote == true;
    /// <summary>TreeView の展開状態（ItemContainerStyle が TwoWay でバインドする）。</summary>
    public bool IsExpanded { get; set; } = true;
    /// <summary>リーフのツールチップ：フルネーム＋上流（あれば）。フォルダには出さない。</summary>
    public string? ToolTip => Branch is null ? null
        : string.IsNullOrEmpty(Branch.Upstream) ? Branch.Name : $"{Branch.Name} → {Branch.Upstream}";
}

/// <summary>
/// ブランチ一覧を "/" 区切りでフォルダ化したツリーへ変換する。
/// ブランチが <see cref="MinBranchesForTree"/> 個未満のときはフォルダ化せずフラットな
/// リーフ列を返す（少数のブランチに階層を挟むと却ってクリックが増えるため）。
/// </summary>
public static class BranchTreeBuilder
{
    public const int MinBranchesForTree = 3;

    public static IReadOnlyList<BranchTreeNode> Build(IReadOnlyList<GitBranchInfo> branches)
    {
        if (branches.Count < MinBranchesForTree)
            return branches.Select(b => new BranchTreeNode { Label = b.Name, Branch = b }).ToList();

        // 入力順（git branch -a の refname ソート）を保ち、フォルダは初出位置に置く
        var roots = new List<BranchTreeNode>();
        foreach (var branch in branches)
        {
            var segments = branch.Name.Split('/');
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
                children = folder.Children;
            }
            children.Add(new BranchTreeNode { Label = segments[^1], Branch = branch });
        }
        return roots;
    }
}
