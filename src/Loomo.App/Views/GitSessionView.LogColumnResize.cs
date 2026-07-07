using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミット一覧（LogList、ListView+GridView）の列幅ドラッグを、ヘッダー行の細い当たり判定だけでなく
/// 一覧本体の高さ全体からも行えるようにする。GridView 標準の "PART_HeaderGripper" はヘッダー行にしか
/// 無いため、同じ役割の透明な <see cref="Thumb"/> を列境界ごとに重ね、一覧の高さいっぱいに伸ばす。
/// </summary>
public partial class GitSessionView
{
    private GridView? _logGridView;
    private ScrollViewer? _logScrollViewer;
    private GridViewHeaderRowPresenter? _logHeaderPresenter;
    private readonly List<Thumb> _logColumnThumbs = new();
    private Border? _logHeaderFillerMask;
    private bool _logColumnResizeReady;

    private void SetupLogColumnResize()
    {
        Loaded += (_, _) => InitLogColumnResize();
    }

    private void InitLogColumnResize()
    {
        if (_logColumnResizeReady) return;
        if (LogList.View is not GridView gridView) return;
        _logScrollViewer = FindScrollViewer(LogList);
        if (_logScrollViewer is null) return;
        _logHeaderPresenter = FindVisualChild<GridViewHeaderRowPresenter>(LogList);
        _logColumnResizeReady = true;
        _logGridView = gridView;

        // GridView が右端の余白へ自動生成する「埋め草」ヘッダー（Role=Padding）は、GridView 自身の
        // ヘッダースタイル差し替えでは届かず既定の白地のまま残る（GridViewHeaderRowPresenter が
        // 独自に描画しており、通常の Style/TargetType では触れない）。同じ見た目の Border を上から
        // 重ねて隠す方が、GridViewHeaderRowPresenter 全体の再テンプレートより低リスク。
        _logHeaderFillerMask = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false,
        };
        _logHeaderFillerMask.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        _logHeaderFillerMask.SetResourceReference(Border.BorderBrushProperty, "Border");
        LogColumnResizeOverlay.Children.Add(_logHeaderFillerMask);

        var thumbStyle = (Style)FindResource("LogColumnResizeThumb");
        foreach (var column in gridView.Columns)
        {
            var thumb = new Thumb { Width = 6, Style = thumbStyle };
            thumb.DragDelta += OnLogColumnThumbDragDelta;
            _logColumnThumbs.Add(thumb);
            LogColumnResizeOverlay.Children.Add(thumb);

            // GridViewColumnHeader 側のドラッグ（PART_HeaderGripper）で幅が変わっても
            // オーバーレイのつまみ位置を追従させる（Width は素の DependencyProperty なので
            // DependencyPropertyDescriptor で変更通知を拾う＝GridView 列幅監視の定石）。
            DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                .AddValueChanged(column, OnLogColumnWidthChanged);
        }

        _logScrollViewer.ScrollChanged += (_, _) => UpdateLogColumnThumbPositions();
        LogList.SizeChanged += (_, _) => UpdateLogColumnThumbPositions();
        UpdateLogColumnThumbPositions();
    }

    private void OnLogColumnWidthChanged(object? sender, EventArgs e) => UpdateLogColumnThumbPositions();

    private void OnLogColumnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_logGridView is null) return;
        var index = _logColumnThumbs.IndexOf((Thumb)sender);
        if (index < 0) return;
        var column = _logGridView.Columns[index];
        column.Width = Math.Max(30, column.ActualWidth + e.HorizontalChange);
    }

    /// <summary>各つまみを列境界（水平スクロール分を差し引いた座標）へ配置し、一覧の全高へ伸ばす。
    /// あわせて、列の合計幅より一覧が広いときに GridView が右端へ残す「埋め草」ヘッダーの上へ
    /// テーマ済みの Border を重ねる（ちょうどヘッダーの高さ・右端まで）。</summary>
    private void UpdateLogColumnThumbPositions()
    {
        if (_logGridView is null || _logScrollViewer is null) return;
        var offset = -_logScrollViewer.HorizontalOffset;
        var x = offset;
        for (var i = 0; i < _logGridView.Columns.Count; i++)
        {
            x += _logGridView.Columns[i].ActualWidth;
            var thumb = _logColumnThumbs[i];
            Canvas.SetLeft(thumb, x - thumb.Width / 2);
            Canvas.SetTop(thumb, 0);
            thumb.Height = LogList.ActualHeight;
        }

        if (_logHeaderFillerMask is null) return;
        var headerHeight = _logHeaderPresenter?.ActualHeight ?? 0;
        // 埋め草ヘッダーの実際の開始位置は列 ActualWidth の合計とわずかにずれる（ヘッダー内部の
        // 罫線・パディング分、数px）。同色なので数px手前から重ねても見た目には影響しない。
        const double overlapBuffer = 12;
        var maskLeft = Math.Max(0, x - overlapBuffer);
        var fillerWidth = Math.Max(0, LogList.ActualWidth - maskLeft);
        Canvas.SetLeft(_logHeaderFillerMask, maskLeft);
        Canvas.SetTop(_logHeaderFillerMask, 0);
        _logHeaderFillerMask.Width = fillerWidth;
        _logHeaderFillerMask.Height = headerHeight;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
