using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミット一覧のグラフ列の幅を変えるつまみ。
///
/// <para><b>つまみは行ごとに持たず、一覧の全高にかかる1本をオーバーレイに置く</b>——列境界のつまみ
/// （<c>GitLogColumnResizeController</c>）と同じ形。行ごとだと、ホバーした1行ぶんの短い線しか光らず
/// 「境界を掴んでいる」ように見えない上に、行の隙間を掴み損ねる。</para>
///
/// <para>位置は実体化済みのグラフセルから引く。グラフの幅は全行で揃っている（列幅は一覧全体の
/// 最大レーン数と上限で決まる）ので、どの行のセルでも右端は同じ x に来る。</para>
///
/// <para>幅は掴んだ瞬間の幅と<b>マウスの位置</b>から決める。<c>DragDelta</c> の変化量を足し込むと、
/// つまみ自身が幅の変化に連れて動くので誤差が溜まる。</para>
/// </summary>
public partial class GitSessionView
{
    private const double GraphGripWidth = 6;

    private Thumb? _graphGrip;
    private CommitGraphCell? _graphGripCell;
    private GridViewHeaderRowPresenter? _graphGripHeader;
    private double _graphDragStartWidth;
    private double _graphDragStartX;

    private void SetupGraphWidthGrip()
    {
        _graphGrip = new Thumb
        {
            Width = GraphGripWidth,
            Style = (Style)FindResource("LogColumnResizeThumb"),
            ToolTip = "ドラッグでグラフの幅を変更（ダブルクリックで元に戻す）",
            Visibility = Visibility.Collapsed,
        };
        _graphGrip.DragStarted += OnGraphWidthDragStarted;
        _graphGrip.DragDelta += OnGraphWidthDragDelta;
        _graphGrip.MouseDoubleClick += OnGraphWidthReset;
        LogColumnResizeOverlay.Children.Add(_graphGrip);

        // LayoutUpdated は一覧に限らず配置のたびに来るので、中身は「値が変わったときだけ書く」軽さに保つ。
        Loaded += (_, _) => LogList.LayoutUpdated += OnGraphGripLayoutUpdated;
        Unloaded += (_, _) => LogList.LayoutUpdated -= OnGraphGripLayoutUpdated;
    }

    private void OnGraphGripLayoutUpdated(object? sender, EventArgs e)
    {
        if (_graphGrip is null) return;
        var cell = GraphGripCell();
        if (cell is null || cell.Row is null || cell.LaneCount <= 0 || Vm?.History.ShowGraph != true)
        {
            if (_graphGrip.Visibility != Visibility.Collapsed) _graphGrip.Visibility = Visibility.Collapsed;
            return;
        }

        var overlay = LogColumnResizeOverlay;
        // セルの右余白（Margin の 4px）の中ほどに置く＝線が丸にも件名にも被らない。
        var right = cell.TranslatePoint(new Point(cell.ActualWidth, 0), overlay).X;
        var left = Math.Round(right + 2 - (GraphGripWidth / 2));
        var top = HeaderBottom(overlay);
        var height = Math.Max(0, LogList.ActualHeight - top);

        if (_graphGrip.Visibility != Visibility.Visible) _graphGrip.Visibility = Visibility.Visible;
        if (Canvas.GetLeft(_graphGrip) != left) Canvas.SetLeft(_graphGrip, left);
        if (Canvas.GetTop(_graphGrip) != top) Canvas.SetTop(_graphGrip, top);
        if (_graphGrip.Height != height) _graphGrip.Height = height;
    }

    /// <summary>位置の手掛かりにするグラフセル。使い回せる限り同じものを使う（毎回木を辿らない）。</summary>
    private CommitGraphCell? GraphGripCell()
    {
        if (_graphGripCell is { IsLoaded: true, IsVisible: true } cached && cached.Row is not null) return cached;
        _graphGripCell = FindDescendant<CommitGraphCell>(LogList, cell => cell.IsVisible && cell.Row is not null);
        return _graphGripCell;
    }

    /// <summary>見出し行の下端（オーバーレイ座標）。つまみは行の範囲だけに掛ける。</summary>
    private double HeaderBottom(UIElement overlay)
    {
        _graphGripHeader ??= FindDescendant<GridViewHeaderRowPresenter>(LogList, _ => true);
        if (_graphGripHeader is not { IsLoaded: true } header) return 0;
        return Math.Round(header.TranslatePoint(new Point(0, header.ActualHeight), overlay).Y);
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> accept) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && accept(match)) return match;
            if (FindDescendant(child, accept) is { } found) return found;
        }
        return null;
    }

    private void OnGraphWidthDragStarted(object sender, DragStartedEventArgs e)
    {
        _graphDragStartWidth = GraphGripCell()?.ActualWidth ?? 0;
        _graphDragStartX = Mouse.GetPosition(LogList).X;
    }

    private void OnGraphWidthDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Vm is not { } vm || GraphGripCell() is not { } cell) return;
        var natural = Math.Max(1, cell.LaneCount) * CommitGraphCell.LaneWidth;
        var width = _graphDragStartWidth + (Mouse.GetPosition(LogList).X - _graphDragStartX);
        width = Math.Max(CommitGraphCell.LaneWidth, width);
        // 全レーンが収まる幅まで広げたら「上限なし」に戻す——以後レーンが増えても素直に広がる。
        vm.History.GraphWidth = width >= natural ? null : Math.Round(width);
    }

    private void OnGraphWidthReset(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.History.GraphWidth = null;
        e.Handled = true;
    }
}
