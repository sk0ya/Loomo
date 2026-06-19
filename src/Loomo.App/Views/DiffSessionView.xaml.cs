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
            _hooked.FileOpenedForJump -= OnFileOpenedForJump;
            _hooked.DiffRows.CollectionChanged -= OnDiffRowsChanged;
            _hooked.SideRows.CollectionChanged -= OnSideRowsChanged;
        }
        _hooked = Vm;
        if (_hooked is not null)
        {
            _hooked.ScrollToRowRequested += ScrollToRow;
            _hooked.FileOpenedForJump += OnFileOpenedForJump;
            _hooked.DiffRows.CollectionChanged += OnDiffRowsChanged;
            _hooked.SideRows.CollectionChanged += OnSideRowsChanged;
            ScheduleRebuildUnified();
            ScheduleRebuildSide();
        }
    }

    private void OnDiffRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuildUnified();
    private void OnSideRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuildSide();

    // ファイルを開いたら、その差分が組み上がった直後に最初の変更へ自動ジャンプする
    private void OnFileOpenedForJump() => _autoJumpPending = true;

    /// <summary>組み立て直後、表示中のビューであれば保留中の自動ジャンプを実行する（レイアウト確定後）。</summary>
    private void MaybeAutoJump(bool sideView)
    {
        if (!_autoJumpPending || Vm is null || Vm.IsSideBySide != sideView) return;
        _autoJumpPending = false;
        Dispatcher.BeginInvoke(new Action(() => Vm?.JumpToFirstChange()), DispatcherPriority.Loaded);
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
        MaybeAutoJump(sideView: false);
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
        MaybeAutoJump(sideView: true);
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
    }

    private static ScrollViewer? InnerScrollViewer(RichTextBox box)
    {
        box.ApplyTemplate();
        return box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
    }

    private void OnSideScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncing || e.VerticalChange == 0) return;
        _syncing = true;
        var offset = ((ScrollViewer)sender).VerticalOffset;
        SetVerticalOffset(_leftTextSv, offset);
        SetVerticalOffset(_rightTextSv, offset);
        SetVerticalOffset(_leftGutterSv, offset);
        SetVerticalOffset(_rightGutterSv, offset);
        _syncing = false;
    }

    private static void SetVerticalOffset(ScrollViewer? sv, double offset)
    {
        if (sv is not null && Math.Abs(sv.VerticalOffset - offset) > 0.5)
            sv.ScrollToVerticalOffset(offset);
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
}
