using System;
using System.Windows;
using System.Windows.Controls;
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
/// <para>色は枝の番号から引く。テーマに依らない固定色にしてあるのは参照バッジ
/// （<c>GitRefKindToBrushConverter</c>）と同じ考え方で、明暗どちらの地の上でも読める彩度に寄せている。</para>
/// </summary>
public sealed class CommitGraphCell : FrameworkElement
{
    /// <summary>レーン1本ぶんの横幅。丸と線が詰まりすぎない最小限。</summary>
    public const double LaneWidth = 12;

    private const double NodeRadius = 3.5;
    private const double LineThickness = 1.6;

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
        // 行コンテナは使い回される（仮想化）ので、中身が差し替わるたびに自分の位置を引き直す。
        DataContextChanged += (_, _) => Resolve();
        Loaded += (_, _) => Resolve();
        Unloaded += (_, _) => Detach();
    }

    private GitHistoryViewModel? _history;

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
            return;
        }

        Attach((list.DataContext as GitSessionViewModel)?.History);
        var index = list.ItemContainerGenerator.IndexFromContainer(container);
        Row = _history?.GraphAt(index);
        LaneCount = Row is null ? 0 : _history?.GraphLaneCount ?? 0;
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

    protected override Size MeasureOverride(Size availableSize)
        => new(Math.Max(0, LaneCount) * LaneWidth, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Row is not { } row || LaneCount <= 0) return;
        var height = ActualHeight;
        if (height <= 0) return;

        var center = height / 2;
        foreach (var edge in row.Edges)
        {
            var pen = PenFor(edge.Color);
            var from = X(edge.FromLane);
            var to = X(edge.ToLane);
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

        if (!row.HasNode) return;
        var x = X(row.Lane);
        drawingContext.DrawEllipse(BrushFor(row.Color), null, new Point(x, center), NodeRadius, NodeRadius);
    }

    private static double X(int lane) => (lane * LaneWidth) + (LaneWidth / 2);

    private static Brush BrushFor(int color) => LaneBrushes[Index(color)];

    /// <summary>ペンは色ぶんだけ作って使い回す（行ごとに作ると、スクロール中に毎フレーム捨てる）。</summary>
    private static readonly Pen[] LanePens = CreatePens();

    private static Pen PenFor(int color) => LanePens[Index(color)];

    private static int Index(int color)
        => ((color % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length;

    private static Pen[] CreatePens()
    {
        var pens = new Pen[LaneBrushes.Length];
        for (var i = 0; i < pens.Length; i++)
        {
            pens[i] = new Pen(LaneBrushes[i], LineThickness);
            pens[i].Freeze();
        }
        return pens;
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
