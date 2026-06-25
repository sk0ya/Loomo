using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Views;

// ペイン内分割（vim 風 Ctrl+W v/s）のビューポート木のノード型。木の操作は ViewportTree、
// 1ペイン内の分割管理は ShellWindow.PaneSplitView が担う。

/// <summary>ビューポート木の1ノード。</summary>
internal abstract class ViewNode
{
    /// <summary>親スプリット内での star 比率。</summary>
    public double Weight { get; set; } = 1;
    /// <summary>直近の描画で割り当てられた Grid トラック番号（未描画は -1）。サイズ取り込み用。</summary>
    public int TrackIndex { get; set; } = -1;
}

/// <summary>リーフ＝1ビューポート。<see cref="TabId"/> のコントロールを <see cref="Container"/> に映す。</summary>
internal sealed class ViewLeaf : ViewNode
{
    /// <summary>ビューポートの安定ID（フォーカス追跡・ナビ用。タブIDとは別）。</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>このビューポートが表示しているタブ。</summary>
    public Guid TabId { get; set; }
    /// <summary>コントロールを内包する枠（フォーカス時にアクセント枠を出す）。再構築で再利用する。</summary>
    public Border Container { get; } = new() { BorderThickness = new Thickness(0), Focusable = false };
}

/// <summary>スプリット＝入れ子の行（上下）または列（左右）。</summary>
internal sealed class ViewSplit : ViewNode
{
    public SplitKind Orientation { get; init; }
    public List<ViewNode> Children { get; } = new();
    public Grid? Host { get; set; }
}
