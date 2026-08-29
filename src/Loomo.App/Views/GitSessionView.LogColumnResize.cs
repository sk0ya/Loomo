using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミット一覧（LogList、ListView+GridView）の列幅まわり。二つの仕事をする。
/// <list type="number">
/// <item>列幅ドラッグを、ヘッダー行の細い当たり判定だけでなく一覧本体の高さ全体から行えるようにする。
///   GridView 標準の "PART_HeaderGripper" はヘッダー行にしか無いため、同じ役割の透明な
///   <see cref="Thumb"/> を列境界ごとに重ね、一覧の高さいっぱいに伸ばす。</item>
/// <item>幅の余りを<b>先頭列（コミット）</b>へ寄せる（<see cref="ApplyLogFillColumn"/>）。日時・作成者・ID は
///   出る文字数がほぼ決まっているので固定幅のまま、伸び縮みするのは読みたい本文＝コミットコメントだけ
///   にする。結果として末尾列（ID）の右境界は常に一覧の右端＝詳細列との GridSplitter と重なり、
///   一覧の右端の区切りは1本だけになる。</item>
/// <item>その固定幅の3列を<b>中身の実寸へ詰める</b>（<see cref="ApplyLogTailColumnWidths"/>）。決め打ちの px を
///   置くと、文字より広ければ列の末尾に読むもののない余白が残り、UI フォントを大きくすれば逆に切れる。
///   実際に並んでいる値と見出しを測って必要な分だけ与え、浮いた幅は先頭列＝コミットへ回す。
///   実寸ぴったりでは隣の列の文字と接するので、列間には常に区切り1本ぶんの隙間
///   （<see cref="ColumnGapGridViewRowPresenter.ColumnGap"/>）を上乗せする。</item>
/// </list>
/// </summary>
public partial class GitSessionView
{
    private GridView? _logGridView;
    private ScrollViewer? _logScrollViewer;
    private GridViewHeaderRowPresenter? _logHeaderPresenter;
    private readonly List<Thumb> _logColumnThumbs = new();
    private Border? _logHeaderFillerMask;
    /// <summary>セルと同じ指定（暗黙スタイルの書体＋DynamicResource の Fs11）だけを持たせた見えない
    /// TextBlock。列幅を測る書体・文字サイズをここから取り、その FontSize の変化＝UI フォントサイズ設定の
    /// 変更（<see cref="Services.UiFontManager.Apply"/> が Application.Resources を書き換える）を拾って
    /// 測り直す。拾わないと文字だけ大きくなり列幅は古いまま＝末尾が切れる。</summary>
    private TextBlock? _logFontProbe;
    /// <summary>幅を測る元コレクション（絞り込み前）の変更購読先。ItemsSource の差し替えで張り替える。</summary>
    private INotifyCollectionChanged? _logMeasureSource;
    private bool _logColumnResizeReady;
    private bool _logFillColumnUpdating;
    private bool _logTailWidthsQueued;
    /// <summary>ユーザーが境界をドラッグして幅を決めた列。以後その列は自動で詰め直さない
    /// （次の履歴読み込みで指定が消えてしまうため）。</summary>
    private readonly HashSet<GridViewColumn> _logUserSizedColumns = new();
    /// <summary>ドラッグ開始からの累積移動量。<see cref="LogUserSizeThresholdPx"/> を超えて初めて
    /// 「ユーザーが幅を決めた」とみなす。</summary>
    private double _logDragTotal;

    /// <summary>自動調整をやめる（＝ユーザー指定とみなす）のに要るドラッグ量。境界付近のクリックに
    /// 紛れる 1px のぶれで、その列が二度と自動調整に戻らなくなるのを防ぐ。</summary>
    private const double LogUserSizeThresholdPx = 3;

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

        // 測る書体・文字サイズの見本。セルの TextBlock と同じく暗黙スタイル（Controls.xaml の
        // FontFamily=Segoe UI）を受け、FontSize だけ DynamicResource の Fs11 を局所指定する
        // ＝セルとまったく同じ解決結果になる。キーが無いテーマでも例外にはならず既定値に落ちる
        // （FindResource は投げるので、フォールバックを書いても効かない）。
        _logFontProbe = new TextBlock { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _logFontProbe.SetResourceReference(TextBlock.FontSizeProperty, "Fs11");
        LogColumnResizeOverlay.Children.Add(_logFontProbe);
        DependencyPropertyDescriptor.FromProperty(TextBlock.FontSizeProperty, typeof(TextBlock))
            .AddValueChanged(_logFontProbe, (_, _) => QueueLogTailColumnWidths());

        var thumbStyle = (Style)FindResource("LogColumnResizeThumb");
        for (var i = 0; i < gridView.Columns.Count; i++)
        {
            var column = gridView.Columns[i];
            var thumb = new Thumb { Width = 6, Style = thumbStyle };
            // 末尾列（ID）の右境界は詳細列との GridSplitter そのものなので、つまみは置かない
            // （＝一覧の右端の区切りは1本だけ）。
            if (i == gridView.Columns.Count - 1) thumb.Visibility = Visibility.Collapsed;
            thumb.DragStarted += (_, _) => _logDragTotal = 0;
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
        // 履歴の読み込みで並ぶ値が変われば必要な幅も変わる。1件ずつの Add で毎回測ると
        // 読み込み1回が O(件数^2) になるので、1拍後に1回だけ測り直す。
        // ItemsSource（＝セッションごとの LogView）が差し替わったら購読も張り替える。
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView))
            .AddValueChanged(LogList, (_, _) => HookLogMeasureSource());
        HookLogMeasureSource();
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
        _logDragTotal += e.HorizontalChange;
        // 意味のある量を動かしてから自動調整の対象外にする（それ未満のぶれで与えた幅は次の測り直しで戻る）。
        if (Math.Abs(_logDragTotal) >= LogUserSizeThresholdPx) _logUserSizedColumns.Add(column);
        column.Width = Math.Max(30, column.ActualWidth - e.HorizontalChange);
    }

    /// <summary>中身の実寸へ詰める列（見出し→測る値）。XAML の<b>並び順ではなく見出し</b>で対応づける
    /// ：添字で結ぶと列を入れ替えた拍子に別の列の値で測っても何も失敗せず、黙って取り違える。
    /// ここに無い見出しの列（先頭の「コミット」）は余りを吸う側なので測らない。</summary>
    private static readonly (string Header, Func<GitLogRow, string?> Value)[] LogTailColumns =
    {
        ("日時", row => row.Date),
        ("作成者", row => row.Author),
        ("ID", row => row.ShortHash),
    };

    /// <summary>その列を実寸へ詰めるか。詰めるなら測る値の取り出し方を返す。</summary>
    private static Func<GitLogRow, string?>? LogTailColumnValue(object? header)
    {
        if (header is not string title) return null;
        foreach (var (name, value) in LogTailColumns)
            if (name == title) return value;
        return null;
    }

    /// <summary>幅を測る元＝<b>絞り込み前</b>の行。ListView.Items（＝フィルタ済みの ICollectionView）を
    /// 測ると、コミット絞り込みの打鍵ごとに列幅が動き、ヒット0件では見出し幅まで潰れる。</summary>
    private IEnumerable LogMeasureRows =>
        LogList.ItemsSource is ICollectionView view ? view.SourceCollection : LogList.Items;

    /// <summary>元コレクションの変更購読を（前の購読を外しつつ）張る。</summary>
    private void HookLogMeasureSource()
    {
        var source = LogMeasureRows as INotifyCollectionChanged ?? LogList.Items;
        if (ReferenceEquals(source, _logMeasureSource)) return;
        if (_logMeasureSource is not null) _logMeasureSource.CollectionChanged -= OnLogMeasureSourceChanged;
        _logMeasureSource = source;
        _logMeasureSource.CollectionChanged += OnLogMeasureSourceChanged;
        QueueLogTailColumnWidths();
    }

    private void OnLogMeasureSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueLogTailColumnWidths();

    /// <summary>幅を測る行数の上限。等幅でも1件ずつ FormattedText を起こすので、長い履歴で
    /// 全件なめない。ここを超える分は横にはみ出しても末尾省略＋ドラッグで足りる。</summary>
    private const int LogTailMeasureRows = 400;

    private void QueueLogTailColumnWidths()
    {
        if (_logTailWidthsQueued) return;
        _logTailWidthsQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _logTailWidthsQueued = false;
            ApplyLogTailColumnWidths();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>末尾の固定幅列を、実際に並んでいる値（＋見出し）の実寸へ詰める。決め打ちの px だと
    /// 文字より広い分がそのまま列の末尾の空白になり、UI フォントを大きくすると今度は切れる。
    /// ユーザーが自分でドラッグした列だけは触らない。</summary>
    private void ApplyLogTailColumnWidths()
    {
        if (_logGridView is null) return;
        var columns = _logGridView.Columns;
        if (columns.Count <= 1) return;

        // 書体と文字サイズは、セルと同じ解決結果になる見本（_logFontProbe）から取る。ListView の
        // FontFamily（Cascadia Mono）で測ると実際の文字より列幅が広くなり末尾に余白が残る
        // ＝セルの TextBlock は暗黙スタイル（Controls.xaml）の Segoe UI が継承値より優先されるため。
        if (_logFontProbe is not { } probe) return;
        var fontSize = probe.FontSize;
        var typeface = new Typeface(probe.FontFamily, probe.FontStyle, probe.FontWeight, probe.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var column in columns)
        {
            if (_logUserSizedColumns.Contains(column)) continue;
            if (LogTailColumnValue(column.Header) is not { } value) continue;

            var content = 0.0;
            var rows = 0;
            foreach (var item in LogMeasureRows)
            {
                if (item is not GitLogRow row) continue;
                if (value(row) is not { Length: > 0 } text) continue;
                content = Math.Max(content, Measure(text));
                if (++rows >= LogTailMeasureRows) break;
            }
            // まだ1件も無いとき（読み込み前）に見出し幅まで詰めない。今の幅のまま読み込みを待つ。
            if (rows == 0) continue;
            var header = column.Header is string title && title.Length > 0 ? Measure(title) : 0;
            var width = LogTailColumnWidth(content, header, ReferenceEquals(column, columns[columns.Count - 1]));
            if (Math.Abs(LogColumnWidth(column) - width) >= 1) column.Width = width;
        }

        double Measure(string text) => new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize,
            Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    /// <summary>実寸へ詰める列に与える幅＝中身と見出しの広いほう＋列間の隙間。隙間を足さないと、
    /// セルの余白を落としている（<see cref="ColumnGapGridViewRowPresenter"/>）ぶん日時の末尾と作成者の
    /// 先頭が字送りゼロで接する。日時は固定書式で全行が同じ幅なので、足りなければ<b>全行</b>が接する。
    /// <b>最後の列（ID）だけは足さない</b>：その右は隣の列ではなく縦スクロールバー（幅7px）と
    /// 詳細列との GridSplitter で、隙間は既にある。足すと ID の後ろだけ2つぶん空いて見える。</summary>
    internal static double LogTailColumnWidth(double contentWidth, double headerWidth, bool isLastColumn)
        => Math.Max(contentWidth, headerWidth)
            + (isLastColumn ? 0 : ColumnGapGridViewRowPresenter.ColumnGap);

    /// <summary>幅の余りを先頭列（コミット）へ寄せて、列の総和を一覧の幅ちょうどに保つ。
    /// GridView には星指定が無いので自前で埋める。</summary>
    private void ApplyLogFillColumn()
    {
        if (_logGridView is null || _logScrollViewer is null || _logFillColumnUpdating) return;
        var columns = _logGridView.Columns;
        if (columns.Count == 0) return;
        var viewport = _logScrollViewer.ViewportWidth;
        if (viewport <= 0) return;

        // ExtentWidth は列の合計よりビューポート幅を優先して返すため、列の合計が少し短いと
        // 「余りなし」と誤判定して GridView 末尾の埋め草が残る。列そのものの合計を使って、
        // 余りを先頭列へ必ず寄せる。
        var extent = 0.0;
        foreach (var column in columns) extent += LogColumnWidth(column);

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
