using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// Diff ペインの「変更ファイル一覧」1ノード。フォルダ階層を持つ（サイドバー Git パネルの
/// <see cref="GitChangeTreeNode"/>・コミット詳細の <see cref="CommitFileNode"/> と同じ組み方）。
/// 選択の正本は VM の <see cref="DiffSessionViewModel.SelectedFile"/>（フラットな <see cref="DiffSessionViewModel.Files"/> の要素）で、
/// このツリーはその見せ方。アドホック比較の項目はパスを持たないことがあるので階層に割らず最上位に並べる。
/// </summary>
public sealed class DiffFileTreeNode : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;

    private DiffFileTreeNode(string name, DiffFileTreeNode? parent, DiffFileItem? file)
    {
        Name = name;
        Parent = parent;
        File = file;
    }

    public string Name { get; private set; }
    public DiffFileTreeNode? Parent { get; private set; }
    /// <summary>ファイル行ならその項目。フォルダ行は null。</summary>
    public DiffFileItem? File { get; }
    public bool IsDirectory => File is null;
    public ObservableCollection<DiffFileTreeNode> Children { get; } = new();

    /// <summary>配下のファイル件数（フォルダ行の右に出す）。</summary>
    public int LeafCount { get; private set; }

    /// <summary>読み直しをまたいで開閉状態を引き継ぐための鍵（フォルダ行だけ意味を持つ）。
    /// 圧縮した見出し（src/Loomo.App）ではなく、根からの相対パスそのもの。</summary>
    public string Key => Parent is null ? Name : $"{Parent.Key}/{Name}";

    public string ToolTipText => File?.DisplayPath ?? Key;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private DiffViewedState _viewedState;

    /// <summary>このファイルをもう読んだか（§24.107）。VM が一覧を組むたび・読み直すたびにパッチの指紋で決め直す。</summary>
    public DiffViewedState ViewedState
    {
        get => _viewedState;
        set
        {
            if (!SetProperty(ref _viewedState, value)) return;
            OnPropertyChanged(nameof(IsViewed));
            OnPropertyChanged(nameof(IsChangedSinceViewed));
            OnPropertyChanged(nameof(ViewedGlyph));
            OnPropertyChanged(nameof(ViewedToolTip));
        }
    }

    /// <summary>印を付けられる行か（アドホック比較・置き場の無いホストでは出さない）。一覧を組むときに VM が決める。</summary>
    public bool CanMarkViewed { get; set; }

    public bool IsViewed => _viewedState == DiffViewedState.Viewed;
    public bool IsChangedSinceViewed => _viewedState == DiffViewedState.ChangedSinceViewed;

    /// <summary>行の右端の印。まだ見ていないものは空（マウスを載せたときだけ ○ を出す・ビュー側）。</summary>
    public string ViewedGlyph => _viewedState switch
    {
        DiffViewedState.Viewed => "✓",
        DiffViewedState.ChangedSinceViewed => "●",
        _ => "○",
    };

    public string ViewedToolTip => _viewedState switch
    {
        DiffViewedState.Viewed => "見た（押すと印を外す）",
        DiffViewedState.ChangedSinceViewed => "見たあとに変わった（押すと、いまの中身を見たことにする）",
        _ => "見たことにする（Alt+V で印を付けて次の未読へ）",
    };

    /// <summary>TreeViewItem.IsSelected と TwoWay で結ぶ。VM 側から選択を移すときにも書く。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// 一覧をフォルダ階層に組み直し、最上位ノードの並びを返す。比較項目は元の順（新しいものが先頭）のまま
    /// 最上位の先頭へ、パスを持つ項目はフォルダ優先・名前順に並べる。
    /// </summary>
    public static IReadOnlyList<DiffFileTreeNode> Build(IEnumerable<DiffFileItem> items)
    {
        var root = new DiffFileTreeNode("", null, null);
        var comparisons = new List<DiffFileTreeNode>();
        foreach (var item in items)
        {
            if (item.IsCompare)
                comparisons.Add(new DiffFileTreeNode(item.DisplayPath, null, item));
            else
                root.Add(item);
        }
        root.CompactAndSort();
        root.Recalculate();
        foreach (var child in root.Children) child.Parent = null;
        return comparisons.Concat(root.Children).ToArray();
    }

    /// <summary>ファイル行を画面に並ぶ順（深さ優先）で列挙する。「次／前のファイル」はこの順で進む。</summary>
    public static IEnumerable<DiffFileTreeNode> Leaves(IEnumerable<DiffFileTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory)
                yield return node;
            foreach (var leaf in Leaves(node.Children))
                yield return leaf;
        }
    }

    /// <summary>フォルダ行を深さ優先で列挙する。</summary>
    public static IEnumerable<DiffFileTreeNode> Directories(IEnumerable<DiffFileTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory) continue;
            yield return node;
            foreach (var dir in Directories(node.Children))
                yield return dir;
        }
    }

    private void Add(DiffFileItem item)
    {
        var parts = item.DisplayPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = this;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var name = parts[i];
            var next = current.Children.FirstOrDefault(n => n.IsDirectory
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new DiffFileTreeNode(name, current, null);
                current.Children.Add(next);
            }
            current = next;
        }
        current.Children.Add(new DiffFileTreeNode(parts.LastOrDefault() ?? item.DisplayPath, current, item));
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
