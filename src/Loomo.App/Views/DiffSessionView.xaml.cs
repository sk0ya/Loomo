using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Diff セッションペイン。AI変更（ファイル変更ジャーナル）と Git 作業ツリー差分を切り替えて表示する。
/// 差分本体は読み取り専用 RichTextBox（FlowDocument）で描き、普通のテキストとして文字単位で選択・コピーできる。
/// データ（<see cref="DiffSessionViewModel.DiffRows"/> / <see cref="DiffSessionViewModel.SideRows"/>）が
/// 変わるたびに FlowDocument を組み直す。左右並びは本文2つ＋行番号ガター2つの縦スクロールを連動させる。
/// </summary>
public partial class DiffSessionView : UserControl
{
    private const double LineHeight = 16.0;
    private const double WidePageWidth = 6000.0; // 折り返しを抑え、左右の行ずれを防ぐ

    // 差分の色（テーマ非依存の固定色。前景のうち文脈/見出しはテーマ追従させる）
    private static readonly Brush AddedBg = Frozen("#1F4CAF50");
    private static readonly Brush AddedFg = Frozen("#FF81C784");
    private static readonly Brush RemovedBg = Frozen("#1FE57373");
    private static readonly Brush RemovedFg = Frozen("#FFE57373");
    private static readonly Brush EmptyBg = Frozen("#14808080");

    private DiffSessionViewModel? _hooked;
    private bool _unifiedDirty;
    private bool _sideDirty;
    private bool _autoJumpPending; // ファイルを開いた直後、組み立て後に最初の変更へ自動ジャンプする

    private ScrollViewer? _unifiedSv;
    private ScrollViewer? _leftGutterSv;
    private ScrollViewer? _leftTextSv;
    private ScrollViewer? _rightGutterSv;
    private ScrollViewer? _rightTextSv;
    private bool _syncing;

    public DiffSessionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private DiffSessionViewModel? Vm => DataContext as DiffSessionViewModel;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    // ===== データ購読・FlowDocument 構築 =====

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hooked is not null)
        {
            _hooked.ScrollToRowRequested -= ScrollToRow;
            _hooked.AutoJumpRequested -= OnAutoJumpRequested;
            _hooked.DiffRows.CollectionChanged -= OnDiffRowsChanged;
            _hooked.SideRows.CollectionChanged -= OnSideRowsChanged;
        }
        _hooked = Vm;
        if (_hooked is not null)
        {
            _hooked.ScrollToRowRequested += ScrollToRow;
            _hooked.AutoJumpRequested += OnAutoJumpRequested;
            _hooked.DiffRows.CollectionChanged += OnDiffRowsChanged;
            _hooked.SideRows.CollectionChanged += OnSideRowsChanged;
            ScheduleRebuildUnified();
            ScheduleRebuildSide();
        }
    }

    private void OnDiffRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuildUnified();
    private void OnSideRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuildSide();

    // ファイルを開く／表示形式を切り替えたら、差分が出来てから最初の変更へ自動ジャンプする
    private void OnAutoJumpRequested()
    {
        _autoJumpPending = true;
        ScheduleAutoJump();
    }

    /// <summary>
    /// 表示中ビューの組み立て（再構築）が保留中なら、それが終わるまで待ってから最初の変更へジャンプする。
    /// キャッシュ命中で再構築が走らない切替でも、既存の FlowDocument に対して確実にジャンプできる。
    /// </summary>
    private void ScheduleAutoJump()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_autoJumpPending || Vm is null) return;
            if (Vm.IsSideBySide ? _sideDirty : _unifiedDirty)
            {
                ScheduleAutoJump(); // 該当ビューの組み立て待ち
                return;
            }
            _autoJumpPending = false;
            Dispatcher.BeginInvoke(new Action(() => Vm?.JumpToAutoTarget()), DispatcherPriority.Loaded);
        }), DispatcherPriority.Background);
    }

    // VM 側は更新のたびに Clear＋逐次 Add するので、複数通知を1回の組み直しに畳む
    private void ScheduleRebuildUnified()
    {
        if (_unifiedDirty) return;
        _unifiedDirty = true;
        Dispatcher.BeginInvoke(new Action(() => { _unifiedDirty = false; RebuildUnified(); }),
            DispatcherPriority.Background);
    }

    private void ScheduleRebuildSide()
    {
        if (_sideDirty) return;
        _sideDirty = true;
        Dispatcher.BeginInvoke(new Action(() => { _sideDirty = false; RebuildSide(); }),
            DispatcherPriority.Background);
    }

    private void RebuildUnified()
    {
        _currentMarks.Clear(); // 旧段落は破棄されるのでマーカー参照も捨てる
        var doc = NewDocument(wide: true);
        if (Vm is not null)
            foreach (var r in Vm.DiffRows)
                doc.Blocks.Add(TextParagraph(r.Text, r.Kind));
        UnifiedBox.Document = doc;
    }

    private void RebuildSide()
    {
        _currentMarks.Clear(); // 旧段落は破棄されるのでマーカー参照も捨てる
        var left = NewDocument(wide: true);
        var right = NewDocument(wide: true);
        var leftNo = NewDocument(wide: false);
        var rightNo = NewDocument(wide: false);
        if (Vm is not null)
            foreach (var s in Vm.SideRows)
            {
                left.Blocks.Add(TextParagraph(s.LeftText, s.LeftKind));
                right.Blocks.Add(TextParagraph(s.RightText, s.RightKind));
                leftNo.Blocks.Add(GutterParagraph(s.LeftLine));
                rightNo.Blocks.Add(GutterParagraph(s.RightLine));
            }
        LeftTextBox.Document = left;
        RightTextBox.Document = right;
        LeftGutter.Document = leftNo;
        RightGutter.Document = rightNo;
        RecomputeSideBlocks();
        PositionCenterGutter();
    }

    private static FlowDocument NewDocument(bool wide)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        if (wide) doc.PageWidth = WidePageWidth;
        return doc;
    }

    private static Paragraph NewParagraph() => new()
    {
        Margin = new Thickness(0),
        LineHeight = LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    /// <summary>本文1行。Kind（DiffLineKind 名／SideCellKind 名）で前景・背景を色分けする。</summary>
    private static Paragraph TextParagraph(string text, string kind)
    {
        var p = NewParagraph();
        var run = new Run(text);
        switch (kind)
        {
            case "Added":
                run.Foreground = AddedFg; p.Background = AddedBg; break;
            case "Removed":
                run.Foreground = RemovedFg; p.Background = RemovedBg; break;
            case "Empty":
                p.Background = EmptyBg; break;
            case "Gap":
                run.SetResourceReference(TextElement.ForegroundProperty, "Accent"); break;
            case "Header":
                run.SetResourceReference(TextElement.ForegroundProperty, "FgDim"); break;
            default: // Context
                run.SetResourceReference(TextElement.ForegroundProperty, "Fg"); break;
        }
        p.Inlines.Add(run);
        return p;
    }

    private static Paragraph GutterParagraph(string number)
    {
        var p = NewParagraph();
        p.TextAlignment = TextAlignment.Right;
        p.Padding = new Thickness(0, 0, 6, 0);
        var run = new Run(number);
        run.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
        p.Inlines.Add(run);
        return p;
    }

    // ===== スクロール連動（左右本文＋行番号ガター） =====

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _unifiedSv = InnerScrollViewer(UnifiedBox);
        _leftGutterSv = InnerScrollViewer(LeftGutter);
        _leftTextSv = InnerScrollViewer(LeftTextBox);
        _rightGutterSv = InnerScrollViewer(RightGutter);
        _rightTextSv = InnerScrollViewer(RightTextBox);
        if (_leftTextSv is not null) _leftTextSv.ScrollChanged += OnSideScrollChanged;
        if (_rightTextSv is not null) _rightTextSv.ScrollChanged += OnSideScrollChanged;
        CenterGutter.SizeChanged += (_, _) => PositionCenterGutter();
    }

    private static ScrollViewer? InnerScrollViewer(RichTextBox box)
    {
        box.ApplyTemplate();
        return box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
    }

    private void OnSideScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && !_syncing)
        {
            _syncing = true;
            var offset = ((ScrollViewer)sender).VerticalOffset;
            SetVerticalOffset(_leftTextSv, offset);
            SetVerticalOffset(_rightTextSv, offset);
            SetVerticalOffset(_leftGutterSv, offset);
            SetVerticalOffset(_rightGutterSv, offset);
            _syncing = false;
        }
        PositionCenterGutter(); // スクロール・サイズ変化に追従して中央ゲターを描き直す
    }

    private static void SetVerticalOffset(ScrollViewer? sv, double offset)
    {
        if (sv is not null && Math.Abs(sv.VerticalOffset - offset) > 0.5)
            sv.ScrollToVerticalOffset(offset);
    }

    // ===== 中央ゲター：変更ブロックの帯＋範囲破棄（Rider 風） =====

    /// <summary>左右並びの変更ブロック（連続する変更行の範囲）。Start/End は SideRows の添字（両端含む）。</summary>
    private readonly record struct SideBlock(int Start, int End, bool HasAdd, bool HasRemove);

    private readonly List<SideBlock> _sideBlocks = new();

    // 帯の色（差分色を薄くしたもの）。modified=アンバー / added=緑 / removed=赤。Hover で濃くする。
    private static readonly Brush BlockModified = Frozen("#33FFB74D");
    private static readonly Brush BlockAdded = Frozen("#3366BB6A");
    private static readonly Brush BlockRemoved = Frozen("#33E57373");
    private static readonly Brush BlockHover = Frozen("#80FFC107");

    /// <summary>SideRows から変更ブロック（連続する非文脈行の範囲）を求めて <see cref="_sideBlocks"/> に貯める。</summary>
    private void RecomputeSideBlocks()
    {
        _sideBlocks.Clear();
        if (Vm is null) return;
        var rows = Vm.SideRows;
        var i = 0;
        while (i < rows.Count)
        {
            if (!IsChangeRow(rows[i])) { i++; continue; }
            var start = i;
            var hasAdd = false;
            var hasRemove = false;
            while (i < rows.Count && IsChangeRow(rows[i]))
            {
                if (rows[i].RightKind == "Added") hasAdd = true;
                if (rows[i].LeftKind == "Removed") hasRemove = true;
                i++;
            }
            _sideBlocks.Add(new SideBlock(start, i - 1, hasAdd, hasRemove));
        }
    }

    /// <summary>変更行か（文脈行・ヘッダ・省略マーカーは変更ブロックに含めない）。</summary>
    private static bool IsChangeRow(DiffSideRowVm row)
        => row.LeftKind is "Added" or "Removed" or "Empty"
           || row.RightKind is "Added" or "Removed" or "Empty";

    /// <summary>中央ゲターに、各変更ブロックの帯と「範囲を戻す」シェブロンを行に合わせて描く。</summary>
    private void PositionCenterGutter()
    {
        CenterGutter.Children.Clear();
        if (_sideBlocks.Count == 0) return;

        var offset = _leftTextSv?.VerticalOffset ?? 0;
        var viewport = CenterGutter.ActualHeight;
        if (viewport <= 0) viewport = _leftTextSv?.ViewportHeight ?? 0;
        var width = CenterGutter.ActualWidth > 0 ? CenterGutter.ActualWidth : 20;
        var canDiscard = Vm?.CanDiscardLines == true;

        foreach (var block in _sideBlocks)
        {
            var top = block.Start * LineHeight - offset;
            var height = (block.End - block.Start + 1) * LineHeight;
            if (top + height < 0 || top > viewport) continue; // 画面外は描かない

            var band = new Border
            {
                Width = width,
                Height = height,
                Background = BandBrush(block),
                Tag = block,
                ToolTip = canDiscard ? "この範囲の変更を破棄（作業ツリーを元に戻す）" : null,
                Cursor = canDiscard ? Cursors.Hand : null,
            };
            // 「»」で左（元）→右（作業ツリー）へ戻すことを示す。破棄できないソースでは帯だけ（情報表示）。
            if (canDiscard)
            {
                band.Child = new TextBlock
                {
                    Text = "»",
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    IsHitTestVisible = false,
                };
                band.MouseEnter += (_, _) => band.Background = BlockHover;
                band.MouseLeave += (_, _) => band.Background = BandBrush(block);
                band.MouseLeftButtonUp += OnCenterBandClicked;
            }
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, top);
            CenterGutter.Children.Add(band);
        }
    }

    private static Brush BandBrush(SideBlock block)
        => block is { HasAdd: true, HasRemove: true } ? BlockModified
            : block.HasAdd ? BlockAdded
            : BlockRemoved;

    /// <summary>中央ゲターの帯クリック：そのブロックが覆う旧/新行番号を集めて範囲破棄する。</summary>
    private async void OnCenterBandClicked(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || sender is not Border { Tag: SideBlock block }) return;
        var rows = vm.SideRows;
        var oldLines = new HashSet<int>();
        var newLines = new HashSet<int>();
        for (var i = block.Start; i <= block.End && i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.LeftKind == "Removed" && int.TryParse(row.LeftLine, out var ol)) oldLines.Add(ol);
            if (row.RightKind == "Added" && int.TryParse(row.RightLine, out var nl)) newLines.Add(nl);
        }
        await vm.DiscardSideLinesAsync(oldLines, newLines);
    }

    // ===== 次/前の変更へジャンプ =====

    /// <summary>現在ジャンプ先としてマーカー表示している段落と、その元の背景。</summary>
    private readonly List<(Paragraph Para, Brush? Original)> _currentMarks = new();
    /// <summary>ジャンプ先の現在行マーカー（差分色とは別の目立つアンバー）。</summary>
    private static readonly Brush CurrentMark = Frozen("#66FFC107");

    /// <summary>表示中（統合 / 左右）の本文で指定行へスクロールし、その行をはっきりマークする。</summary>
    private void ScrollToRow(int index)
    {
        ClearCurrentMarks();
        if (Vm is { IsSideBySide: true })
        {
            // 左右本文＋両ガターを横一列にマークして、どの行を指しているか帯で示す
            MarkAndScroll(LeftTextBox, _leftTextSv, index);
            MarkOnly(RightTextBox, index);
            MarkOnly(LeftGutter, index);
            MarkOnly(RightGutter, index);
        }
        else
        {
            MarkAndScroll(UnifiedBox, _unifiedSv, index);
        }
    }

    /// <summary>対象段落をマークし、画面の上から約1/3の位置に来るようスクロールする。</summary>
    private void MarkAndScroll(RichTextBox box, ScrollViewer? sv, int index)
    {
        if (BlockAt(box, index) is not { } para) return;
        Mark(para);
        if (sv is null) return;
        var rect = para.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var target = sv.VerticalOffset + rect.Top - sv.ViewportHeight * 0.35;
        sv.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private void MarkOnly(RichTextBox box, int index)
    {
        if (BlockAt(box, index) is { } para) Mark(para);
    }

    private void Mark(Paragraph para)
    {
        _currentMarks.Add((para, para.Background));
        para.Background = CurrentMark;
    }

    /// <summary>前回のジャンプ先マーカーを消して元の差分色へ戻す。</summary>
    private void ClearCurrentMarks()
    {
        foreach (var (para, original) in _currentMarks)
            para.Background = original;
        _currentMarks.Clear();
    }

    private static Paragraph? BlockAt(RichTextBox box, int index)
    {
        var blocks = box.Document.Blocks;
        if (index < 0 || index >= blocks.Count) return null;
        return blocks.ElementAt(index) as Paragraph;
    }

    // ===== ファイル一覧 =====

    /// <summary>ファイル行ダブルクリック：エディタで開く。</summary>
    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { SelectedFile: { } file } vm)
            vm.OpenInEditorCommand.Execute(file);
    }

    // ===== 選択行の破棄（統合表示） =====

    /// <summary>統合表示で選択している行の変更だけを破棄する。段落の並びは <see cref="DiffSessionViewModel.DiffRows"/> と1対1。</summary>
    private async void OnDiscardSelectedLines(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var rows = SelectedUnifiedRowIndices();
        if (rows.Count == 0) return;
        await vm.DiscardSelectedLinesAsync(rows);
    }

    /// <summary>本文の選択範囲が覆う段落（＝差分行）の添字集合を返す。キャレットだけのときはその1行。</summary>
    private IReadOnlySet<int> SelectedUnifiedRowIndices()
    {
        var set = new HashSet<int>();
        var doc = UnifiedBox.Document;
        var startPara = UnifiedBox.Selection.Start.Paragraph;
        if (startPara is null) return set;
        var endPara = UnifiedBox.Selection.End.Paragraph ?? startPara;

        var startIdx = IndexOfBlock(doc, startPara);
        var endIdx = IndexOfBlock(doc, endPara);
        if (startIdx < 0 || endIdx < 0) return set;
        if (endIdx < startIdx) (startIdx, endIdx) = (endIdx, startIdx);
        for (var i = startIdx; i <= endIdx; i++) set.Add(i);
        return set;
    }

    private static int IndexOfBlock(FlowDocument doc, Block target)
    {
        var i = 0;
        foreach (var block in doc.Blocks)
        {
            if (ReferenceEquals(block, target)) return i;
            i++;
        }
        return -1;
    }
}
