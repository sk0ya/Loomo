using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
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
    private const double PageWidthPadding = 24.0; // 本文右端の余白（横スクロールの行き過ぎ防止）
    private const double MinContentWidth = 200.0; // 計測不能時の最小ページ幅

    // 本文の最長行を測る等幅タイプフェース（FlowDocument と同じ Cascadia Mono / Consolas）
    private Typeface? _monoTypeface;

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
    private bool _viewHooked;   // 子コントロールへの購読済みフラグ（Loaded は再ペアレントで再入する）

    public DiffSessionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ===== エディタ配色の購読 =====
    // エディタの配色を変えたら差分の構文色も付け直す。ただし購読先は**静的イベント**なので、
    // コンストラクタで張って外さないと、ペインを作り直しても・別ウィンドウへ切り離しても
    // このビューがプロセスの最後まで生き残り、配色変更のたびに死んだビューまで組み直してしまう。
    // そこで「表示されている間だけ購読」し、離れている間に配色が変わっていたら復帰時に付け直す
    // （Loaded/Unloaded は再ペアレントのたびに走るので、多重購読を防ぐフラグは要る）。
    private bool _colorsHooked;
    private int _colorsGeneration = EditorSyntaxColors.Generation;

    private void HookSyntaxColors()
    {
        if (!_colorsHooked)
        {
            EditorSyntaxColors.Changed += OnEditorSyntaxColorsChanged;
            _colorsHooked = true;
        }
        if (_colorsGeneration != EditorSyntaxColors.Generation)
            OnEditorSyntaxColorsChanged();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_colorsHooked) return;
        EditorSyntaxColors.Changed -= OnEditorSyntaxColorsChanged;
        _colorsHooked = false;
        _colorsGeneration = EditorSyntaxColors.Generation;
    }

    private void OnEditorSyntaxColorsChanged()
    {
        _colorsGeneration = EditorSyntaxColors.Generation;
        ScheduleRebuildUnified();
        ScheduleRebuildSide();
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
            _hooked.Conflict.ScrollToConflictRequested -= OnScrollToConflictRequested;
            _hooked.DiffRows.CollectionChanged -= OnDiffRowsChanged;
            _hooked.SideRows.CollectionChanged -= OnSideRowsChanged;
        }
        _hooked = Vm;
        if (_hooked is not null)
        {
            _hooked.ScrollToRowRequested += ScrollToRow;
            _hooked.AutoJumpRequested += OnAutoJumpRequested;
            _hooked.Conflict.ScrollToConflictRequested += OnScrollToConflictRequested;
            _hooked.DiffRows.CollectionChanged += OnDiffRowsChanged;
            _hooked.SideRows.CollectionChanged += OnSideRowsChanged;
            ScheduleRebuildUnified();
            ScheduleRebuildSide();
        }
    }

    /// <summary>コンフリクトのナビゲーション（前へ/次へ・自動フォーカス）：対象の <see cref="ConflictRegionVm"/> の
    /// コンテナを見えるところまでスクロールする。ConflictBlocks はまとめて Add されるため、コンテナ生成が
    /// 完了するレイアウトパス後まで待つ（AutoJump と同じ理由）。</summary>
    private void OnScrollToConflictRequested(int regionIndex)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Vm is null) return;
            var region = Vm.Conflict.ConflictBlocks.OfType<ConflictRegionVm>().FirstOrDefault(r => r.Index == regionIndex);
            if (region is null) return;
            if (ConflictItemsControl.ItemContainerGenerator.ContainerFromItem(region) is FrameworkElement fe)
                fe.BringIntoView();
        }), DispatcherPriority.ContextIdle);
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
        _contextRowIndex = -1; // 行が入れ替わったので、覚えていた右クリック行はもう別の行を指す
        // ページ幅を最長行ぴったりに合わせる（折り返さず、横スクロールの可動域も実内容に一致させる）
        var width = Vm is null ? MinContentWidth : MeasureMaxWidth(Vm.DiffRows.Select(r => r.Text));
        var doc = NewDocument(width);
        if (Vm is not null)
        {
            var syntax = DiffSyntaxHighlighter.ForUnified(Vm.SyntaxFilePath, Vm.UnifiedHasPatchPrefix, Vm.DiffRows);
            for (var i = 0; i < Vm.DiffRows.Count; i++)
                doc.Blocks.Add(TextParagraph(Vm.DiffRows[i].Text, Vm.DiffRows[i].Kind, TokensAt(syntax, i)));
        }
        UnifiedBox.Document = doc;
    }

    private void RebuildSide()
    {
        _currentMarks.Clear(); // 旧段落は破棄されるのでマーカー参照も捨てる
        _contextRowIndex = -1; // 行が入れ替わったので、覚えていた右クリック行はもう別の行を指す
        // 左右で同じページ幅にして、横スクロールの連動が座標としてぴったり揃うようにする
        var width = Vm is null
            ? MinContentWidth
            : MeasureMaxWidth(Vm.SideRows.SelectMany(s => new[] { s.LeftText, s.RightText }));
        var left = NewDocument(width);
        var right = NewDocument(width);
        var leftNo = NewDocument(null);
        var rightNo = NewDocument(null);
        if (Vm is not null)
        {
            var leftSyntax = DiffSyntaxHighlighter.ForSide(Vm.SyntaxFilePath, Vm.SideRows, left: true);
            var rightSyntax = DiffSyntaxHighlighter.ForSide(Vm.SyntaxFilePath, Vm.SideRows, left: false);
            for (var i = 0; i < Vm.SideRows.Count; i++)
            {
                var s = Vm.SideRows[i];
                left.Blocks.Add(TextParagraph(s.LeftText, s.LeftKind, TokensAt(leftSyntax, i)));
                right.Blocks.Add(TextParagraph(s.RightText, s.RightKind, TokensAt(rightSyntax, i)));
                leftNo.Blocks.Add(GutterParagraph(s.LeftLine));
                rightNo.Blocks.Add(GutterParagraph(s.RightLine));
            }
        }
        LeftTextBox.Document = left;
        RightTextBox.Document = right;
        LeftGutter.Document = leftNo;
        RightGutter.Document = rightNo;
        RecomputeSideBlocks();
        PositionCenterGutter();
    }

    private static FlowDocument NewDocument(double? pageWidth)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = UiFontManager.Scaled(12),
        };
        if (pageWidth is double w) doc.PageWidth = w; // 行番号ガター（null）は内容幅に自動フィット
        return doc;
    }

    /// <summary>最長行の表示幅（px）を測ってページ幅を決める。CJK 全角も正しく測れるよう FormattedText を使う。
    /// 測るサイズは本文と同じ<b>スケール後</b>のフォントサイズでなければならない——等倍の 12 で測ると、
    /// UI 文字サイズを上げているときにページ幅が実際より狭くなって本文だけが折り返し、
    /// 折り返さない行番号ガターと1行ずつずれていく（行番号が信用できなくなる）。</summary>
    private double MeasureMaxWidth(IEnumerable<string> lines)
    {
        var typeface = _monoTypeface ??= new Typeface(
            new FontFamily("Cascadia Mono, Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fontSize = UiFontManager.Scaled(12);
        var max = 0.0;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var ft = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, pixelsPerDip);
            if (ft.WidthIncludingTrailingWhitespace > max) max = ft.WidthIncludingTrailingWhitespace;
        }
        return Math.Max(MinContentWidth, max + PageWidthPadding);
    }

    private static Paragraph NewParagraph() => new()
    {
        Margin = new Thickness(0),
        LineHeight = LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    /// <summary>行 <paramref name="index"/> のトークン列（範囲外・色付けしない差分では null）。</summary>
    private static SyntaxToken[]? TokensAt(IReadOnlyList<SyntaxToken[]?> syntax, int index)
        => index < syntax.Count ? syntax[index] : null;

    /// <summary>
    /// 本文1行。Kind（DiffLineKind 名／SideCellKind 名）で前景・背景を色分けする。
    /// <paramref name="tokens"/> があるときは前景をエディタと同じ構文色に譲り、追加／削除は<b>背景の色</b>で
    /// 示す（前景まで緑・赤にすると構文色が潰れる。追加/削除の区別は帯・行番号・統合表示の ± が担う）。
    /// </summary>
    private static Paragraph TextParagraph(string text, string kind, SyntaxToken[]? tokens = null)
    {
        var p = NewParagraph();
        p.Background = kind switch
        {
            "Added" => AddedBg,
            "Removed" => RemovedBg,
            "Empty" => EmptyBg,
            _ => null,
        };
        if (tokens is { Length: > 0 })
        {
            p.Inlines.AddRange(SyntaxRuns(text, tokens));
            return p;
        }
        var run = new Run(text);
        switch (kind)
        {
            case "Added": run.Foreground = AddedFg; break;
            case "Removed": run.Foreground = RemovedFg; break;
            case "Gap": run.SetResourceReference(TextElement.ForegroundProperty, "Accent"); break;
            case "Header": run.SetResourceReference(TextElement.ForegroundProperty, "FgDim"); break;
            default: run.SetResourceReference(TextElement.ForegroundProperty, "Fg"); break; // Context / Empty
        }
        p.Inlines.Add(run);
        return p;
    }

    /// <summary>本番の色引き（エディタの配色）。行ごとにデリゲートを作らないよう1つを使い回す。</summary>
    private static readonly Func<TokenKind, Brush?> ThemeForeground = EditorSyntaxColors.Foreground;

    /// <summary>
    /// 1行をトークンごとの <see cref="Run"/> へ割る。色が同じ区間は1つの Run にまとめる——差分は1ファイル分の
    /// 行を一度に組むので、記号や識別子のたびに Run を作ると FlowDocument の要素数が跳ね上がる。
    /// 色を持たない種別（<paramref name="foreground"/> が null を返す）はアプリのテーマ色 <c>Fg</c>。
    /// 色引きを差し替えられるのはテストのため——共有のエディタ配色（<see cref="EditorTheme"/> の静的な
    /// ブラシ）は最初に触ったスレッドの持ち物になるので、割り方の検証でそれを初期化させない。
    /// </summary>
    internal static List<Run> SyntaxRuns(string text, SyntaxToken[] tokens, Func<TokenKind, Brush?>? foreground = null)
    {
        foreground ??= ThemeForeground;
        // まず行を隙間なく区間へ割る（トークンの間＝色を持たない区間も埋める）
        var segments = new List<(int Start, int End, Brush? Brush)>();
        var position = 0;
        foreach (var token in tokens)
        {
            // 重なり・逆順のトークンが来ても行の文字列を壊さないよう、常に「ここまで」以降だけを見る
            var start = Math.Clamp(token.StartColumn, position, text.Length);
            var end = Math.Clamp(start + token.Length, start, text.Length);
            if (end == start) continue;
            if (start > position) segments.Add((position, start, null));
            segments.Add((start, end, foreground(token.Kind)));
            position = end;
        }
        if (position < text.Length) segments.Add((position, text.Length, null));

        var runs = new List<Run>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end, brush) = segments[i];
            while (i + 1 < segments.Count && ReferenceEquals(segments[i + 1].Brush, brush))
                end = segments[++i].End;   // 同じ色の隣り合う区間は1つの Run にまとめる
            var run = new Run(text[start..end]);
            if (brush is not null) run.Foreground = brush;
            else run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            runs.Add(run);
        }
        return runs;
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
        HookSyntaxColors();
        // 以下（自分の子コントロールへの購読）は1度だけ——Loaded はペインの再ペアレントのたびに走り、
        // 二度目からは同じハンドラが積み上がるだけで、子は同じインスタンスのまま生き続けている。
        if (_viewHooked) return;
        _viewHooked = true;

        _unifiedSv = InnerScrollViewer(UnifiedBox);
        _leftGutterSv = InnerScrollViewer(LeftGutter);
        _leftTextSv = InnerScrollViewer(LeftTextBox);
        _rightGutterSv = InnerScrollViewer(RightGutter);
        _rightTextSv = InnerScrollViewer(RightTextBox);
        if (_leftTextSv is not null) _leftTextSv.ScrollChanged += OnSideScrollChanged;
        if (_rightTextSv is not null) _rightTextSv.ScrollChanged += OnSideScrollChanged;
        CenterGutter.SizeChanged += (_, _) => PositionCenterGutter();

        // Shift+ホイールで横スクロール（FlowDocumentScrollViewer は既定で横ホイールを扱わない）
        UnifiedBox.PreviewMouseWheel += OnTextPreviewMouseWheel;
        LeftTextBox.PreviewMouseWheel += OnTextPreviewMouseWheel;
        RightTextBox.PreviewMouseWheel += OnTextPreviewMouseWheel;

        // 右クリックした「行」を覚える（メニューを開くとキャレット位置には頼れないため、
        // 押した座標から段落を引く）。「この行をエディタで開く」の対象になる。
        UnifiedBox.PreviewMouseRightButtonDown += OnBodyRightButtonDown;
        LeftTextBox.PreviewMouseRightButtonDown += OnBodyRightButtonDown;
        RightTextBox.PreviewMouseRightButtonDown += OnBodyRightButtonDown;
        // キーボード（メニューキー／Shift+F10）で開いたときはマウス座標が無いのでキャレット行を対象にする。
        UnifiedBox.ContextMenuOpening += OnBodyContextMenuOpening;
        LeftTextBox.ContextMenuOpening += OnBodyContextMenuOpening;
        RightTextBox.ContextMenuOpening += OnBodyContextMenuOpening;
    }

    /// <summary>Shift 押下中のホイールを横スクロールに割り当てる。左右本文はスクロール連動で他方も追従する。</summary>
    private void OnTextPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        var sv = sender switch
        {
            var s when ReferenceEquals(s, LeftTextBox) => _leftTextSv,
            var s when ReferenceEquals(s, RightTextBox) => _rightTextSv,
            _ => _unifiedSv,
        };
        if (sv is null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? InnerScrollViewer(RichTextBox box)
    {
        box.ApplyTemplate();
        return box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
    }

    private void OnSideScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_syncing && (e.VerticalChange != 0 || e.HorizontalChange != 0))
        {
            _syncing = true;
            var src = (ScrollViewer)sender;
            if (e.VerticalChange != 0)
            {
                var offset = src.VerticalOffset;
                SetVerticalOffset(_leftTextSv, offset);
                SetVerticalOffset(_rightTextSv, offset);
                SetVerticalOffset(_leftGutterSv, offset);
                SetVerticalOffset(_rightGutterSv, offset);
            }
            if (e.HorizontalChange != 0) // 左右本文の横スクロールを連動（行番号ガターは固定）
            {
                // ペイン幅や縦スクロールバーの有無が違うと ScrollableWidth も左右で異なる。
                // 操作元だけが相手の上限を越えないよう、双方が到達可能な範囲へ揃える。
                var offset = _leftTextSv is not null && _rightTextSv is not null
                    ? ClampToSharedHorizontalRange(
                        src.HorizontalOffset,
                        _leftTextSv.ScrollableWidth,
                        _rightTextSv.ScrollableWidth)
                    : Math.Clamp(src.HorizontalOffset, 0, src.ScrollableWidth);
                SetHorizontalOffset(_leftTextSv, offset);
                SetHorizontalOffset(_rightTextSv, offset);
            }
            _syncing = false;
        }
        PositionCenterGutter(); // スクロール・サイズ変化に追従して中央ゲターを描き直す
    }

    private static void SetVerticalOffset(ScrollViewer? sv, double offset)
    {
        if (sv is not null && Math.Abs(sv.VerticalOffset - offset) > 0.5)
            sv.ScrollToVerticalOffset(offset);
    }

    private static void SetHorizontalOffset(ScrollViewer? sv, double offset)
    {
        if (sv is not null && Math.Abs(sv.HorizontalOffset - offset) > 0.5)
            sv.ScrollToHorizontalOffset(offset);
    }

    internal static double ClampToSharedHorizontalRange(
        double requestedOffset,
        double leftScrollableWidth,
        double rightScrollableWidth)
        => Math.Clamp(requestedOffset, 0, Math.Min(leftScrollableWidth, rightScrollableWidth));

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
                    FontSize = UiFontManager.Scaled(13),
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

    /// <summary>コンフリクトの Result 欄をクリック/フォーカスしたら、そのコンフリクトを「現在地」にする
    /// （ツールバーの Ours/両方/Theirs/適用 ボタンの対象を合わせる）。</summary>
    private void OnConflictResultGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConflictRegionVm region } && Vm is not null)
            Vm.Conflict.FocusConflictRegion(region);
    }

    // ===== ファイル一覧 =====

    /// <summary>ファイル行ダブルクリック：エディタで開く。</summary>
    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { SelectedFile: { } file } vm)
            vm.OpenInEditorCommand.Execute(file);
    }

    /// <summary>右クリックしたメニュー項目が属する行のファイル項目（一覧の行メニュー用）。</summary>
    private static DiffFileItem? ContextItem(object sender)
        => sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: DiffFileItem item } } }
            ? item
            : null;

    private void OnOpenFileInEditor(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && ContextItem(sender) is { } item) vm.OpenInEditorCommand.Execute(item);
    }

    private void OnCompareFileWithClipboard(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && ContextItem(sender) is { } item)
            vm.CompareFileWithClipboardCommand.Execute(item);
    }

    private void OnCopyFilePath(object sender, RoutedEventArgs e)
    {
        if (ContextItem(sender) is not { FullPath.Length: > 0 } item) return;
        try { Clipboard.SetText(item.FullPath); }
        catch { /* 他アプリがクリップボードを掴んでいるだけ。無視してよい。 */ }
    }

    private void OnDiscardFile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && ContextItem(sender) is { } item) vm.DiscardCommand.Execute(item);
    }

    private void OnRevertFile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && ContextItem(sender) is { } item) vm.RevertCommand.Execute(item);
    }

    private void OnCloseComparison(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && ContextItem(sender) is { } item) vm.CloseComparisonCommand.Execute(item);
    }

    private void OnCloseAllComparisons(object sender, RoutedEventArgs e)
        => Vm?.CloseAllComparisonsCommand.Execute(null);

    // ===== 差分本体の右クリック（この行をエディタで開く／選択範囲を比較） =====

    /// <summary>右クリックした行（表示中の形式での <c>DiffRows</c> / <c>SideRows</c> の添字）。-1 は不明。</summary>
    private int _contextRowIndex = -1;

    private void OnBodyRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextRowIndex = -1;
        if (sender is not RichTextBox box) return;
        // 行の右端より外側でも同じ行として拾えるよう snapToText で最寄りの位置を取る。
        if (box.GetPositionFromPoint(e.GetPosition(box), snapToText: true) is not { } position
            || position.Paragraph is not { } paragraph)
            return;
        _contextRowIndex = IndexOfBlock(box.Document, paragraph);
    }

    /// <summary>
    /// キーボードで開いたメニュー（マウス座標が無い＝<c>CursorLeft/Top</c> が負）は、直前の右クリックで
    /// 覚えた行がそのまま残っていると別ファイルの行を指しかねない。キャレットのある行に取り直す。
    /// </summary>
    private void OnBodyContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.CursorLeft >= 0 || e.CursorTop >= 0) return;   // マウスで開いた（上で取得済み）
        _contextRowIndex = sender is RichTextBox box && box.CaretPosition.Paragraph is { } paragraph
            ? IndexOfBlock(box.Document, paragraph)
            : -1;
    }

    private void OnOpenRowInEditor(object sender, RoutedEventArgs e)
        => Vm?.RequestOpenRowInEditor(_contextRowIndex);

    /// <summary>差分本体で選択したテキストを左に、クリップボードを右に置いて比較し直す。</summary>
    private void OnCompareSelectionWithClipboard(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var box = (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: RichTextBox target } ? target : null;
        var selected = box?.Selection.Text ?? "";
        if (string.IsNullOrEmpty(selected))
        {
            vm.SetStatusMessage("差分本体でテキストを選択してから実行してください。", isError: true);
            return;
        }
        if (ClipboardText.TryGet() is not { } clipboard)
        {
            vm.SetStatusMessage("クリップボードにテキストがありません。", isError: true);
            return;
        }
        // 出どころのファイルは付けない：左は差分本体の切れ端で、その1行目は元ファイルの1行目ではないため、
        // 行番号を引く相手にできない（付けると「この行をエディタで開く」が無関係な行を開く）。
        vm.ShowComparison(new DiffComparison("差分の選択範囲", selected, "クリップボード", clipboard));
    }

    private void OnSwapComparison(object sender, RoutedEventArgs e) => Vm?.SwapComparisonCommand.Execute(null);

    private void OnRecompareWithClipboard(object sender, RoutedEventArgs e)
        => Vm?.RecompareWithClipboardCommand.Execute(null);

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
