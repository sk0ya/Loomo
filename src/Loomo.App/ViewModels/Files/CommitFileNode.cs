using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Git;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// コミット詳細の「変更ファイル一覧」1ノード。フォルダ階層を持つ（サイドバー Git パネルの
/// <see cref="GitChangeTreeNode"/> と同じ組み方だが、こちらは読むだけなのでチェック状態を持たない）。
/// </summary>
public sealed class CommitFileNode : ObservableObject
{
    private bool _isExpanded = true;

    private CommitFileNode(string name, CommitFileNode? parent, CommitFileStat? file)
    {
        Name = name;
        Parent = parent;
        File = file;
    }

    public string Name { get; private set; }
    public CommitFileNode? Parent { get; private set; }
    public CommitFileStat? File { get; }
    public bool IsDirectory => File is null;
    public ObservableCollection<CommitFileNode> Children { get; } = new();

    /// <summary>配下のファイル件数（フォルダ行の右に出す）。</summary>
    public int LeafCount { get; private set; }

    /// <summary>クリックで開く相対パス。フォルダ行は null。</summary>
    public string? NavigatePath => File?.Path;

    public string ChurnLabel => File?.ChurnLabel ?? "";

    /// <summary>行のツールチップ。リネームは git の綴り（変更前 → 変更後）も見せる。</summary>
    public string ToolTipText => File is { } file
        ? (file.IsRenamed ? $"{file.DisplayPath}\n{file.Path}" : file.Path)
        : Name;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>ファイル一覧をフォルダ階層に組み直し、最上位ノードの並びを返す。</summary>
    public static IReadOnlyList<CommitFileNode> Build(IEnumerable<CommitFileStat> files)
    {
        var root = new CommitFileNode("", null, null);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            root.Add(file);
        root.CompactAndSort();
        root.Recalculate();
        return root.Children.ToArray();
    }

    private void Add(CommitFileStat file)
    {
        var parts = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = this;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var name = parts[i];
            var next = current.Children.FirstOrDefault(n => n.IsDirectory
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new CommitFileNode(name, current, null);
                current.Children.Add(next);
            }
            current = next;
        }
        current.Children.Add(new CommitFileNode(parts.LastOrDefault() ?? file.Path, current, file));
    }

    /// <summary>子がフォルダ1つだけの階層を src/Loomo.App のようにまとめ、狭い列で縦長になるのを防ぐ。</summary>
    private void CompactAndSort()
    {
        foreach (var child in Children.ToArray()) child.CompactAndSort();
        if (Parent is not null)
        {
            while (Children.Count == 1 && Children[0].IsDirectory)
            {
                var only = Children[0];
                Name += "/" + only.Name;
                Children.Clear();
                foreach (var grandChild in only.Children)
                {
                    grandChild.Parent = this;
                    Children.Add(grandChild);
                }
            }
        }
        var ordered = Children.OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Children.Clear();
        foreach (var child in ordered) Children.Add(child);
    }

    private void Recalculate()
    {
        if (Children.Count == 0)
        {
            LeafCount = IsDirectory ? 0 : 1;
            return;
        }
        var total = 0;
        foreach (var child in Children)
        {
            child.Recalculate();
            total += child.LeafCount;
        }
        LeafCount = total;
    }
}
