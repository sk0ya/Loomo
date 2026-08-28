using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミット一覧（LogList、ListView+GridView）の列幅まわり。二つの仕事をする。
/// <list type="number">
/// <item>列幅ドラッグを、ヘッダー行の細い当たり判定だけでなく一覧本体の高さ全体から行えるようにする。
///   GridView 標準の "PART_HeaderGripper" はヘッダー行にしか無いため、同じ役割の透明な
///   <see cref="Thumb"/> を列境界ごとに重ね、一覧の高さいっぱいに伸ばす。</item>
/// <item>幅の余りを<b>先頭列（コミット）</b>へ寄せる（<see cref="ApplyLogFillColumn"/>）。作成者・日時は
///   出る文字数がほぼ決まっているので固定幅のまま、伸び縮みするのは読みたい本文＝コミットコメントだけ
///   にする。結果として末尾列（日時）の右境界は常に一覧の右端＝詳細列との GridSplitter と重なり、
///   一覧の右端の区切りは1本だけになる。</item>
/// </list>
/// </summary>
public partial class GitSessionView
{
    private GridView? _logGridView;
    private ScrollViewer? _logScrollViewer;
    private GridViewHeaderRowPresenter? _logHeaderPresenter;
    private readonly List<Thumb> _logColumnThumbs = new();
    private Border? _logHeaderFillerMask;
    private bool _logColumnResizeReady;
    private bool _logFillColumnUpdating;

    /// <summary>余りを引き受ける列（先頭＝コミット）を詰められる下限。ここまで縮んだら横スクロールに任せる。</summary>
    private const double LogFillColumnMinWidth = 120;

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
        for (var i = 0; i < gridView.Columns.Count; i++)
        {
            var column = gridView.Columns[i];
            var thumb = new Thumb { Width = 6, Style = thumbStyle };
            // 末尾列（日時）の右境界は詳細列との GridSplitter そのものなので、つまみは置かない
            // （＝一覧の右端の区切りは1本だけ）。
            if (i == gridView.Columns.Count - 1) thumb.Visibility = Visibility.Collapsed;
            thumb.DragDelta += OnLogColumnThumbDragDelta;
            _logColumnThumbs.Add(thumb);
            LogColumnResizeOverlay.Children.Add(thumb);

            // 列幅が変わったらつまみ位置と余りの寄せ直しを走らせる（Width は素の DependencyProperty
            // なので DependencyPropertyDescriptor で変更通知を拾う＝GridView 列幅監視の定石）。
            DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                .AddValueChanged(column, OnLogColumnWidthChanged);
        }

        _logScrollViewer.ScrollChanged += (_, _) => UpdateLogColumnThumbPositions();
        LogList.SizeChanged += (_, _) => UpdateLogColumnThumbPositions();
        HideHeaderGrippers();
        UpdateLogColumnThumbPositions();
    }

    /// <summary>見出しの既定テンプレートが持つ列幅ドラッグつまみ（PART_HeaderGripper）を全部隠す。
    /// あれは「掴んだ境界の<b>左</b>の列」を直接太らせる作りで、余りを先頭列へ寄せる本ファイルの
    /// モデルとは逆向きに動いてしまう（掴んだ境界が指と反対へ逃げる）。境界のドラッグは見出し行の
    /// 上まで覆っているオーバーレイのつまみが受けるので、操作そのものは失われない。
    /// 見出しの実体化が1拍遅れることがあるため、見つからなければ一度だけ再試行する。</summary>
    private void HideHeaderGrippers(bool retry = true)
    {
        if (_logHeaderPresenter is null) return;

        var found = false;
        foreach (var header in FindVisualChildren<GridViewColumnHeader>(_logHeaderPresenter))
        {
            if (header.Template?.FindName("PART_HeaderGripper", header) is not Thumb gripper) continue;
            gripper.Visibility = Visibility.Collapsed;
            found = true;
        }
        if (found || !retry) return;

        Dispatcher.BeginInvoke(new Action(() => HideHeaderGrippers(false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnLogColumnWidthChanged(object? sender, EventArgs e) => UpdateLogColumnThumbPositions();

    /// <summary>境界のドラッグ。動かすのは掴んだ境界の<b>右</b>の列（幅の総和は一覧の幅に固定されて
    /// いるので、右隣を細くすれば余りが先頭列へ回り、境界そのものが指について動く）。左の列を太らせる
    /// 素直な実装だと、その分を先頭列が吐き出して境界が指と逆へ逃げてしまう。</summary>
    private void OnLogColumnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_logGridView is null) return;
        var index = _logColumnThumbs.IndexOf((Thumb)sender);
        if (index < 0 || index + 1 >= _logGridView.Columns.Count) return;
        var column = _logGridView.Columns[index + 1];
        column.Width = Math.Max(30, column.ActualWidth - e.HorizontalChange);
    }

    /// <summary>幅の余りを先頭列（コミット）へ寄せて、列の総和を一覧の幅ちょうどに保つ。
    /// GridView には星指定が無いので自前で埋める。</summary>
    private void ApplyLogFillColumn()
    {
        if (_logGridView is null || _logScrollViewer is null || _logFillColumnUpdating) return;
        var columns = _logGridView.Columns;
        if (columns.Count == 0) return;
        var viewport = _logScrollViewer.ViewportWidth;
        var extent = _logScrollViewer.ExtentWidth;
        if (viewport <= 0 || extent <= 0) return;

        // 現在幅は ActualWidth を使う（ExtentWidth はレイアウト後の実寸から出ているので、
        // 同じ世代の値どうしで引き算しないと余りを二重に足してしまう）。
        var fillColumn = columns[0];
        var current = fillColumn.ActualWidth;
        var target = LogFillColumnWidth(current, viewport, extent);
        // 遊びを 1px 強とるのは、レイアウト丸めで ±1px 未満の差が残り続けるときに
        // 「幅を入れる→再レイアウト→また差が出る」を延々繰り返さないため（見た目には出ない差）。
        if (Math.Abs(current - target) < 1.5) return;

        // Width の変更通知は再び UpdateLogColumnThumbPositions を呼ぶので、その内側で
        // もう一度ここへ入って往復しないようフラグで抑える。
        _logFillColumnUpdating = true;
        try { fillColumn.Width = target; }
        finally { _logFillColumnUpdating = false; }
    }

    /// <summary>余りを引き受ける列に与える幅。ビューポートと中身（ExtentWidth）の差＝余りを今の幅へ
    /// そのまま足す（負なら詰める）。行やヘッダーの内側の余白を定数で見積もらずに済み、縦スクロール
    /// バーの出入りにも次のレイアウトで追従する。これ以上は詰められない下限で止め、そこから先は
    /// 横スクロールに任せる（コミットの件名を潰して読めなくしない）。</summary>
    internal static double LogFillColumnWidth(double currentWidth, double viewportWidth, double extentWidth)
        => Math.Max(LogFillColumnMinWidth, currentWidth + (viewportWidth - extentWidth));

    /// <summary>列の現在幅。Width を入れた直後は ActualWidth がまだ古い（次のレイアウトで反映される）
    /// ので、明示指定があるときは Width を正とする。</summary>
    private static double LogColumnWidth(GridViewColumn column)
        => double.IsNaN(column.Width) ? column.ActualWidth : column.Width;

    /// <summary>各つまみを列境界（水平スクロール分を差し引いた座標）へ配置し、一覧の全高へ伸ばす。
    /// あわせて、列の合計幅より一覧が広いときに GridView が右端へ残す「埋め草」ヘッダーの上へ
    /// テーマ済みの Border を重ねる（ちょうどヘッダーの高さ・右端まで）。</summary>
    private void UpdateLogColumnThumbPositions()
    {
        if (_logGridView is null || _logScrollViewer is null) return;
        ApplyLogFillColumn();
        var offset = -_logScrollViewer.HorizontalOffset;
        var x = offset;
        for (var i = 0; i < _logGridView.Columns.Count; i++)
        {
            x += LogColumnWidth(_logGridView.Columns[i]);
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
        // 余りは先頭列が吸うので通常は埋め草が無い。ここでマスクを出すと末尾列の見出しに被るため、
        // 実際に余白があるときだけ描く。
        if (LogList.ActualWidth - x <= overlapBuffer + 1)
        {
            _logHeaderFillerMask.Width = 0;
            _logHeaderFillerMask.Height = 0;
            return;
        }
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var found in FindVisualChildren<T>(child)) yield return found;
        }
    }
}
