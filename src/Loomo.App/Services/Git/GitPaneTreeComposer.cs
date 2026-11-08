using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Git ペイン左列のツリーを組む：ブランチのツリーの後ろに「ワークツリー」「タグ」「サブモジュール」の見出しを足し、
/// ブランチの行にはそれを置いているワークツリーの印を付ける（§24.17.3）。
///
/// <para>以前は左列の下段に切替式の一覧（WT／タグ／サブ）を別に持っていたが、本命のブランチ一覧の高さを
/// 常に削り、作成・比較・片付けのボタンまで並んで、見たいものが見えなかった。ワークツリーは
/// 「ブランチを置いてある場所」なのでブランチの行の印に、タグ・サブモジュールは origin と同じく
/// 畳んだ見出しにする（触らない間は1行しか取らない）。</para>
///
/// <para><b>同じ中身なら同じインスタンスを返す</b>——<c>RefreshAsync</c> はファイルの保存でも走り、
/// 毎回新しいリストを入れると TreeView がコンテナを作り直して選択と開きかけのメニューが落ちる
/// （§24.15 のブランチ絞り込みと同じ理由）。ワークツリーは件数（±N）が読み直しのたびに変わりうるので、
/// 行の同一性はパスで見て、中身は行の <see cref="BranchTreeNode.Worktree"/> だけ差し替える。</para>
/// </summary>
public static class GitPaneTreeComposer
{
    public const string WorktreeSectionLabel = "ワークツリー";
    public const string TagSectionLabel = "タグ";
    public const string SubmoduleSectionLabel = "サブモジュール";

    /// <summary>
    /// ペインのツリーを組む。足すものが無ければ <paramref name="branchTree"/> そのもの。
    /// <paramref name="term"/> があるとき（絞り込み中）は見出しを挟まず、一致した項目を平らに並べる
    /// （<see cref="BranchTreeBuilder.BuildFiltered"/> と同じ考え）。
    /// </summary>
    public static IReadOnlyList<BranchTreeNode> Compose(
        IReadOnlyList<BranchTreeNode> previous,
        IReadOnlyList<BranchTreeNode> branchTree,
        string? term,
        IReadOnlyList<GitBranchInfo> allBranches,
        IReadOnlyList<GitWorktreeInfo> worktrees,
        IReadOnlyList<GitTagInfo> tags,
        IReadOnlyList<GitSubmoduleInfo> submodules)
    {
        var t = term?.Trim();
        var filtering = !string.IsNullOrEmpty(t);
        var standalone = StandaloneWorktrees(worktrees, allBranches)
            .Where(w => !filtering || Matches(w.DisplayName, t) || Matches(w.Path, t)).ToList();
        var matchedTags = tags.Where(tag => !filtering || Matches(tag.Name, t)).ToList();
        var matchedSubmodules = submodules.Where(s => !filtering || Matches(s.Path, t)).ToList();

        if (standalone.Count == 0 && matchedTags.Count == 0 && matchedSubmodules.Count == 0)
            return branchTree;

        var extras = previous.Skip(CountPrefix(previous, branchTree)).ToList();
        var references = filtering
            ? FlatReferences(extras, standalone, matchedTags, matchedSubmodules)
            : Sections(extras, standalone, matchedTags, matchedSubmodules);

        if (CountPrefix(previous, branchTree) == branchTree.Count
            && previous.Count == branchTree.Count + references.Count
            && references.Select((node, i) => ReferenceEquals(node, previous[branchTree.Count + i])).All(same => same))
            return previous;

        return branchTree.Concat(references).ToList();
    }

    /// <summary>
    /// ブランチの行（全体・絞り込みのどちらでも）に、それを<b>別の場所に</b>置いているワークツリーの印を付ける。
    /// いまの場所のワークツリーは付けない（現在ブランチの ● と同じことを2回言うだけ）。
    /// </summary>
    public static void ApplyWorktreeMarks(
        IEnumerable<BranchTreeNode> nodes,
        IReadOnlyList<GitWorktreeInfo> worktrees,
        IReadOnlyDictionary<string, string> agentMarks,
        IReadOnlyDictionary<string, string>? runMarks = null)
    {
        foreach (var node in nodes)
        {
            if (node.Branch is { IsRemote: false } branch)
            {
                var holder = worktrees.FirstOrDefault(w => !w.IsCurrent && !w.IsBare
                    && string.Equals(w.Branch, branch.Name, StringComparison.Ordinal));
                node.Worktree = holder;
                node.AgentMark = AgentMarkFor(holder, agentMarks);
                node.RunMark = RunMarkFor(holder, runMarks);
            }
            else if (node.IsWorktreeItem)
            {
                node.AgentMark = AgentMarkFor(node.Worktree, agentMarks);
                node.RunMark = RunMarkFor(node.Worktree, runMarks);
            }
            ApplyWorktreeMarks(node.Children, worktrees, agentMarks, runMarks);
        }
    }

    // 印の鍵はパスの末尾の区切りを落とした形（WorktreeRuns.Marks と同じ）。大文字小文字は辞書が無視する。
    private static string RunMarkFor(GitWorktreeInfo? worktree, IReadOnlyDictionary<string, string>? marks)
        => worktree is not null && marks is not null
           && marks.TryGetValue(System.IO.Path.TrimEndingDirectorySeparator(worktree.Path), out var mark) ? mark : "";

    /// <summary>
    /// ブランチの行に載らないワークツリー（デタッチ・ブランチ一覧に無いブランチ）。いまの場所と bare は出さない。
    /// 判定は<b>絞り込む前の全ブランチ</b>で行う——絞り込んだツリーで見ると、一致しなかっただけのブランチの
    /// ワークツリーが「ブランチを持たない」側へ紛れ込む。
    /// </summary>
    public static IEnumerable<GitWorktreeInfo> StandaloneWorktrees(
        IReadOnlyList<GitWorktreeInfo> worktrees, IReadOnlyList<GitBranchInfo> allBranches)
    {
        var locals = allBranches.Where(b => !b.IsRemote).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        return worktrees.Where(w => !w.IsCurrent && !w.IsBare
            && (w.Branch is null || !locals.Contains(w.Branch)));
    }

    private static string AgentMarkFor(GitWorktreeInfo? worktree, IReadOnlyDictionary<string, string> marks)
        => worktree is not null && marks.TryGetValue(worktree.Path, out var mark) ? mark : "";

    private static bool Matches(string? text, string? term)
        => text is not null && term is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="previous"/> の先頭が <paramref name="branchTree"/> と同じノードで並んでいる数。</summary>
    private static int CountPrefix(IReadOnlyList<BranchTreeNode> previous, IReadOnlyList<BranchTreeNode> branchTree)
    {
        var n = 0;
        while (n < previous.Count && n < branchTree.Count && ReferenceEquals(previous[n], branchTree[n])) n++;
        return n;
    }

    private static List<BranchTreeNode> Sections(
        List<BranchTreeNode> previous,
        IReadOnlyList<GitWorktreeInfo> worktrees,
        IReadOnlyList<GitTagInfo> tags,
        IReadOnlyList<GitSubmoduleInfo> submodules)
    {
        var sections = new List<BranchTreeNode>();
        if (worktrees.Count > 0)
            sections.Add(Section(previous, WorktreeSectionLabel, WorktreeLeaves(
                previous.FirstOrDefault(n => n is { IsSection: true, Label: WorktreeSectionLabel })?.Children ?? new(),
                worktrees)));
        if (tags.Count > 0)
            sections.Add(Section(previous, TagSectionLabel, Leaves(
                previous.FirstOrDefault(n => n is { IsSection: true, Label: TagSectionLabel })?.Children ?? new(),
                tags, tag => new BranchTreeNode { Label = tag.Name, Tag = tag }, n => n.Tag)));
        if (submodules.Count > 0)
            sections.Add(Section(previous, SubmoduleSectionLabel, Leaves(
                previous.FirstOrDefault(n => n is { IsSection: true, Label: SubmoduleSectionLabel })?.Children ?? new(),
                submodules, sub => new BranchTreeNode { Label = sub.Path, Submodule = sub }, n => n.Submodule)));
        return sections;
    }

    /// <summary>見出しを1つ組む。中身（子の並び）が前回と同じなら前回の見出しを返し、違えば開閉だけ引き継ぐ。
    /// ワークツリーの見出しは開いておく（数が少なく、置いている事実がまず見えてほしい）、タグ・サブは畳む。</summary>
    private static BranchTreeNode Section(List<BranchTreeNode> previous, string label, List<BranchTreeNode> children)
    {
        var old = previous.FirstOrDefault(n => n.IsSection && n.Label == label);
        if (old is not null && old.Children.Count == children.Count
            && old.Children.Zip(children).All(pair => ReferenceEquals(pair.First, pair.Second)))
            return old;
        var section = new BranchTreeNode
        {
            Label = label,
            IsSection = true,
            IsExpanded = old?.IsExpanded ?? label == WorktreeSectionLabel,
        };
        section.Children.AddRange(children);
        return section;
    }

    private static List<BranchTreeNode> Leaves<T>(
        List<BranchTreeNode> previous, IReadOnlyList<T> items,
        Func<T, BranchTreeNode> create, Func<BranchTreeNode, T?> itemOf) where T : class
        => items.Select(item => previous.FirstOrDefault(n => Equals(itemOf(n), item)) ?? create(item)).ToList();

    private static List<BranchTreeNode> WorktreeLeaves(List<BranchTreeNode> previous, IReadOnlyList<GitWorktreeInfo> worktrees)
        => worktrees.Select(w =>
        {
            var node = previous.FirstOrDefault(n => n.IsWorktreeItem && n.Worktree?.IsSamePath(w.Path) == true)
                ?? new BranchTreeNode { Label = WorktreeLabel(w), IsWorktreeItem = true };
            node.Worktree = w;
            return node;
        }).ToList();

    private static string WorktreeLabel(GitWorktreeInfo w) => $"{w.DisplayName}  {w.FolderName}";

    private static List<BranchTreeNode> FlatReferences(
        List<BranchTreeNode> previous,
        IReadOnlyList<GitWorktreeInfo> worktrees,
        IReadOnlyList<GitTagInfo> tags,
        IReadOnlyList<GitSubmoduleInfo> submodules)
    {
        var nodes = WorktreeLeaves(previous, worktrees);
        nodes.AddRange(Leaves(previous, tags, tag => new BranchTreeNode { Label = tag.Name, Tag = tag }, n => n.Tag));
        nodes.AddRange(Leaves(previous, submodules,
            sub => new BranchTreeNode { Label = sub.Path, Submodule = sub }, n => n.Submodule));
        return nodes;
    }
}
