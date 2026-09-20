using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Views;

/// <summary>ビューポート木（<see cref="ViewNode"/>）の純粋な構造操作：リーフ列挙・挿入・除去・正規化と、
/// 要素を親から外すヘルパ。レンダリングや状態（フォーカス・ホスト）は <see cref="ShellWindow.PaneSplitView"/>。
/// トップレベルのペイン木に対する <see cref="sk0ya.Loomo.App.Layout.PaneLayoutTree"/> の、分割ビューポート版。</summary>
internal static class ViewportTree
{
    /// <summary>木のリーフ（ビューポート）を深さ優先で列挙する。</summary>
    public static IEnumerable<ViewLeaf> Leaves(ViewNode? node)
    {
        if (node is ViewLeaf leaf)
            yield return leaf;
        else if (node is ViewSplit split)
            foreach (var child in split.Children)
                foreach (var l in Leaves(child))
                    yield return l;
    }

    /// <summary><paramref name="node"/> を <paramref name="target"/> の隣（同方向スプリットなら同列、
    /// それ以外は新スプリットで包む）へ挿入し、新しいルートを返す。</summary>
    public static ViewNode? Insert(ViewNode? root, ViewNode target, ViewNode node, SplitKind orientation)
    {
        var parent = FindParent(root, target);
        if (parent is not null && parent.Orientation == orientation)
        {
            parent.Children.Insert(parent.Children.IndexOf(target) + 1, node);
            return root;
        }

        var split = new ViewSplit { Orientation = orientation, Weight = target.Weight };
        target.Weight = 1;
        node.Weight = 1;
        split.Children.Add(target);
        split.Children.Add(node);
        if (parent is null)
            return split;
        parent.Children[parent.Children.IndexOf(target)] = split;
        return root;
    }

    /// <summary><paramref name="node"/> を親スプリットから取り外し、新しいルートを返す（畳み込みは Normalize に任せる）。</summary>
    public static ViewNode? Remove(ViewNode? root, ViewNode node)
    {
        var parent = FindParent(root, node);
        if (parent is null)
            return null;
        parent.Children.Remove(node);
        return root;
    }

    /// <summary>空スプリットを除去し、子1つのスプリットを畳み、同方向の入れ子をフラット化する。</summary>
    public static ViewNode? Normalize(ViewNode? node)
    {
        if (node is not ViewSplit split)
            return node;

        var kids = new List<ViewNode>();
        foreach (var child in split.Children)
        {
            var n = Normalize(child);
            if (n is null)
                continue;
            if (n is ViewSplit inner && inner.Orientation == split.Orientation)
            {
                var total = inner.Children.Sum(c => c.Weight > 0 ? c.Weight : 1);
                var scale = total > 0 ? (inner.Weight > 0 ? inner.Weight : 1) / total : 1;
                foreach (var c in inner.Children)
                {
                    c.Weight = (c.Weight > 0 ? c.Weight : 1) * scale;
                    kids.Add(c);
                }
            }
            else
            {
                kids.Add(n);
            }
        }

        if (kids.Count == 0)
            return null;
        if (kids.Count == 1)
        {
            kids[0].Weight = split.Weight;
            return kids[0];
        }
        split.Children.Clear();
        split.Children.AddRange(kids);
        return split;
    }

    /// <summary>要素を現在の親（Panel / Decorator / ContentControl）から外す。再ペアレント前の片付けに使う。</summary>
    public static void Detach(FrameworkElement el)
    {
        switch (el.Parent)
        {
            case Panel p: p.Children.Remove(el); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }

    private static ViewSplit? FindParent(ViewNode? root, ViewNode target)
    {
        if (root is not ViewSplit split)
            return null;
        if (split.Children.Contains(target))
            return split;
        foreach (var child in split.Children)
            if (FindParent(child, target) is { } found)
                return found;
        return null;
    }
}
