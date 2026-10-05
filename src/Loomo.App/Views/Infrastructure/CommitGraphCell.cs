using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミット一覧の先頭列に置く、1行ぶんのグラフ。<c>git log --graph</c> の ASCII の代わりに
/// レーン（<see cref="GitCommitGraph"/>）を自分で描く。
///
/// <para><b>幅は行ごとではなく一覧全体で揃える</b>——行ごとの実幅にすると、枝の増減で件名の
/// 左端がガタガタ動いて読めない。<see cref="LaneCount"/> には一覧の最大レーン数を渡す。</para>
///
/// <para><b>押すと経路を出す</b>（<see cref="GitCommitRoute"/>）。経路に乗る線と丸は太く、それ以外は
/// 薄く描く——色はそのまま残すので、どの枝を通ってきたかは色でも追える。</para>
///
/// <para><b>幅には上限を付けられる</b>（<see cref="GitHistoryViewModel.GraphWidth"/>）。上限より
/// レーンが多ければ、まずレーンの間隔を詰め（<see cref="MinLaneWidth"/> まで）、それでも収まらない
/// 右側は切る。</para>
///
/// <para>色は枝の番号から引く。テーマに依らない固定色にしてあるのは参照バッジ
/// （<c>GitRefKindToBrushConverter</c>）と同じ考え方で、明暗どちらの地の上でも読める彩度に寄せている。</para>
/// </summary>
public sealed class CommitGraphCell : FrameworkElement
{
    /// <summary>レーン1本ぶんの横幅。丸と線が詰まりすぎない最小限。</summary>
    public const double LaneWidth = 12;

    /// <summary>幅の上限に合わせて詰めるときの下限。これより詰めると丸同士が重なって見分けられない。</summary>
    public const double MinLaneWidth = 5;

    private const double NodeRadius = 3.5;
    private const double LineThickness = 1.6;
    private const double RouteLineThickness = 2.8;
    private const double RouteNodeRadius = 4.5;

    /// <summary>経路を出している間、経路の外の線と丸に掛ける不透明度。</summary>
    private const double DimOpacity = 0.22;

    private static readonly Brush[] LaneBrushes =
    [
        Freeze("#6CB6FF"),   // 青
        Freeze("#73C991"),   // 緑
        Freeze("#E2C08D"),   // 金
        Freeze("#C792EA"),   // 紫
        Freeze("#E57373"),   // 赤
        Freeze("#4DD0E1"),   // 水
    ];

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GitGraphRow), typeof(CommitGraphCell),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LaneCountProperty = DependencyProperty.Register(
        nameof(LaneCount), typeof(int), typeof(CommitGraphCell),
        new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>この行のグラフ。null なら何も描かない（絞り込み中など）。</summary>
    public GitGraphRow? Row
    {
        get => (GitGraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>一覧全体の最大レーン数（＝この列の幅を決める）。</summary>
    public int LaneCount
    {
        get => (int)GetValue(LaneCountProperty);
        set => SetValue(LaneCountProperty, value);
    }

    public CommitGraphCell()
    {
        Cursor = Cursors.Hand;
        ToolTip = HintText;
        ToolTipService.SetInitialShowDelay(this, 900);
        MouseLeave += (_, _) => CloseRouteTip();
        // 行コンテナは使い回される（仮想化）ので、中身が差し替わるたびに自分の位置を引き直す。
        DataContextChanged += (_, _) => Resolve();
        Loaded += (_, _) => Resolve();
        Unloaded += (_, _) => Detach();
    }

    private const string HintText = "クリックでこのコミットの経路（どのマージを通って先端へ届いたか）を表示";

    private GitHistoryViewModel? _history;
    private ToolTip? _routeTip;
    private GitLogRow? _commit;
    private double? _widthLimit;

    /// <summary>
    /// 自分が一覧の何行目かを引き当て、その行のグラフを取る。
    ///
    /// <para>グラフは行（<see cref="GitLogRow"/>）ではなく<b>一覧の並び</b>に属する情報なので、
    /// DataTemplate のバインディングでは取れない——行だけ見ても前後の枝は分からない。
    /// コンテナから位置を引くのが、仮想化と両立する唯一の経路。</para>
    /// </summary>
    private void Resolve()
    {
        var container = ItemsControl.ContainerFromElement(null, this) as ListViewItem;
        if (container is null || ItemsControl.ItemsControlFromItemContainer(container) is not { } list)
        {
            Row = null;
            _commit = null;
            return;
        }

        Attach((list.DataContext as GitSessionViewModel)?.History);
        var index = list.ItemContainerGenerator.IndexFromContainer(container);
        Row = _history?.GraphAt(index);
        LaneCount = Row is null ? 0 : _history?.GraphLaneCount ?? 0;
        _commit = container.Content as GitLogRow;
        var limit = _history?.GraphWidth;
        if (_widthLimit != limit)
        {
            _widthLimit = limit;
            InvalidateMeasure();
        }
        // 経路は行の中身ではなく一覧全体の状態なので、Row が同じでも描き直す。
        InvalidateVisual();
        // 押したときのツールチップは経路に追従させる（Esc で畳めば閉じ、矢印キーで動けば中身が変わる）。
        if (_routeTip is not null)
        {
            if (_history?.RouteSummary is { } summary) _routeTip.Content = summary;
            else CloseRouteTip();
        }
    }

    private void Attach(GitHistoryViewModel? history)
    {
        if (ReferenceEquals(_history, history)) return;
        Detach();
        _history = history;
        if (_history is not null) _history.GraphChanged += OnGraphChanged;
    }

    private void Detach()
    {
        if (_history is null) return;
        _history.GraphChanged -= OnGraphChanged;
        _history = null;
    }

    private void OnGraphChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Resolve));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Handled にしない——行の選択はそのまま ListView に任せる（経路は選択に追従する）。
        // 修飾キー付きは複数選択の操作なので経路には触らない。
        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None
            && _commit is { IsCommit: true } commit && _history is { } history && Row is not null)
        {
            history.ToggleRouteFrom(commit);
            CloseRouteTip();
            // 押したその場で道のりを出す（待ち時間のあるホバーのツールチップを待たせない）。
            if (history.RouteSummary is { } summary)
            {
                _routeTip = new ToolTip { Content = summary, PlacementTarget = this, Placement = PlacementMode.Bottom };
                _routeTip.IsOpen = true;
            }
        }
    }

    /// <summary>ホバーのツールチップ：経路を出している間は道のり、そうでなければ操作の案内。</summary>
    protected override void OnToolTipOpening(ToolTipEventArgs e)
    {
        if (_routeTip is { IsOpen: true })
        {
            e.Handled = true; // 押したときのものを出している
            return;
        }
        ToolTip = _history?.RouteSummary ?? HintText;
        base.OnToolTipOpening(e);
    }

    private void CloseRouteTip()
    {
        if (_routeTip is null) return;
        _routeTip.IsOpen = false;
        _routeTip = null;
    }

    /// <summary>この列の幅（全レーンぶんか、上限か）。</summary>
    private double CellWidth
    {
        get
        {
            var natural = Math.Max(0, LaneCount) * LaneWidth;
            return _widthLimit is { } limit ? Math.Min(natural, Math.Max(limit, LaneWidth)) : natural;
        }
    }

    /// <summary>レーン1本の間隔。上限があればそこへ収まるよう詰める（下限 <see cref="MinLaneWidth"/>）。</summary>
    private double Spacing => LaneCount <= 0
        ? LaneWidth
        : Math.Clamp(CellWidth / LaneCount, MinLaneWidth, LaneWidth);

    protected override Size MeasureOverride(Size availableSize) => new(CellWidth, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var height = ActualHeight;
        if (Row is not { } row || LaneCount <= 0 || height <= 0) return;

        var width = CellWidth;
        // 透明の地を敷く——線の無い隙間も押せるように（既定のヒットテストは描いた線と丸の上だけ）。
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

        var spacing = Spacing;
        var route = _history?.Route;
        var center = height / 2;
        // 経路の線は後から重ねる（薄い線の下に潜らないように）。
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var edge in row.Edges)
            {
                var onRoute = route?.Contains(edge) == true;
                if ((pass == 1) != onRoute) continue;
                var pen = route is null ? PenFor(edge.Color)
                    : onRoute ? RoutePenFor(edge.Color)
                    : DimPenFor(edge.Color);
                var from = X(edge.FromLane, spacing);
                var to = X(edge.ToLane, spacing);
                switch (edge.Kind)
                {
                    case GitGraphEdgeKind.Through:
                        drawingContext.DrawLine(pen, new Point(from, 0), new Point(from, height));
                        break;
                    case GitGraphEdgeKind.In:
                        // 上端からこの行の丸へ。列が違えば斜めに寄る（枝の合流）。
                        drawingContext.DrawLine(pen, new Point(from, 0), new Point(to, center));
                        break;
                    case GitGraphEdgeKind.Out:
                        drawingContext.DrawLine(pen, new Point(from, center), new Point(to, height));
                        break;
                }
            }
        }

        if (row.HasNode) DrawNode(drawingContext, row, route, new Point(X(row.Lane, spacing), center));
        drawingContext.Pop();
    }

    private void DrawNode(DrawingContext dc, GitGraphRow row, GitCommitRoute? route, Point point)
    {
        var hash = _commit?.Hash;
        if (route is null)
        {
            dc.DrawEllipse(BrushFor(row.Color), null, point, NodeRadius, NodeRadius);
            return;
        }
        if (!route.Contains(hash))
        {
            dc.DrawEllipse(DimBrushFor(row.Color), null, point, NodeRadius, NodeRadius);
            return;
        }
        dc.DrawEllipse(BrushFor(row.Color), null, point, RouteNodeRadius, RouteNodeRadius);
        // 起点には輪を掛ける（＝どこから辿っているか）。輪は文字色＝地の明暗に追従させる。
        if (hash == route.Focus)
        {
            var ring = new Pen(TryFindResource("Fg") as Brush ?? Brushes.White, 1.5);
            dc.DrawEllipse(null, ring, point, RouteNodeRadius + 2, RouteNodeRadius + 2);
        }
    }

    private static double X(int lane, double spacing) => (lane * spacing) + (spacing / 2);

    private static Brush BrushFor(int color) => LaneBrushes[Index(color)];

    private static Brush DimBrushFor(int color) => DimBrushes[Index(color)];

    /// <summary>ペンは色ぶんだけ作って使い回す（行ごとに作ると、スクロール中に毎フレーム捨てる）。</summary>
    private static readonly Pen[] LanePens = CreatePens(LaneBrushes, LineThickness);

    private static readonly Brush[] DimBrushes = LaneBrushes.Select(brush => Fade(brush, DimOpacity)).ToArray();
    private static readonly Pen[] DimPens = CreatePens(DimBrushes, LineThickness);
    private static readonly Pen[] RoutePens = CreatePens(LaneBrushes, RouteLineThickness);

    private static Pen PenFor(int color) => LanePens[Index(color)];

    private static Pen DimPenFor(int color) => DimPens[Index(color)];

    private static Pen RoutePenFor(int color) => RoutePens[Index(color)];

    private static int Index(int color)
        => ((color % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length;

    private static Pen[] CreatePens(Brush[] brushes, double thickness)
    {
        var pens = new Pen[brushes.Length];
        for (var i = 0; i < pens.Length; i++)
        {
            pens[i] = new Pen(brushes[i], thickness);
            pens[i].Freeze();
        }
        return pens;
    }

    private static Brush Fade(Brush brush, double opacity)
    {
        var faded = brush.Clone();
        faded.Opacity = opacity;
        faded.Freeze();
        return faded;
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
