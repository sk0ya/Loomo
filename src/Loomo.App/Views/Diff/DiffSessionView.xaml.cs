using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;
using System;
using System.Collections.Generic;
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
/// Diff セッションペイン。Git 作業ツリー差分とアドホック比較を切り替えて表示する。
/// 差分本体は読み取り専用 RichTextBox（FlowDocument）で描き、普通のテキストとして文字単位で選択・コピーできる。
/// データ（<see cref="DiffSessionViewModel.DiffRows"/> / <see cref="DiffSessionViewModel.SideRows"/>）が
/// 変わるたびに FlowDocument を組み直す。左右並びは本文2つ＋行番号ガター2つの縦スクロールを連動させる。
/// </summary>
public partial class DiffSessionView : UserControl, IDisposable
{
    private readonly DiffMarkdownRenderController _markdownRenderController;
    private readonly DiffFlowDocumentRenderer _documentRenderer;
    private readonly DiffDocumentBuildController _documentBuildController;
    private readonly DiffSideBlockPresenter _sideBlockPresenter;
    private readonly DiffRowNavigationPresenter _rowNavigationPresenter;
    private readonly DiffAutoJumpController _autoJumpController;
    private readonly DiffScrollSyncController _scrollSyncController;
    private readonly DiffSessionBindingController _bindingController;

    private ScrollViewer? _unifiedSv;
    private ScrollViewer? _leftGutterSv;
    private ScrollViewer? _leftTextSv;
    private ScrollViewer? _rightGutterSv;
    private ScrollViewer? _rightTextSv;
    private bool _viewHooked;   // 子コントロールへの購読済みフラグ（Loaded は再ペアレントで再入する）

    public DiffSessionView()
    {
        InitializeComponent();
        _documentRenderer = new DiffFlowDocumentRenderer(this);
        _sideBlockPresenter = new DiffSideBlockPresenter(
            CenterGutter,
            () => Vm,
            () => _leftTextSv?.VerticalOffset ?? 0,
            () => CenterGutter.ActualHeight > 0
                ? CenterGutter.ActualHeight
                : _leftTextSv?.ViewportHeight ?? 0,
            () => CenterGutter.ActualWidth > 0 ? CenterGutter.ActualWidth : 20);
        _markdownRenderController = new DiffMarkdownRenderController(MarkdownRenderHost, Dispatcher);
        _markdownRenderController.LinkClicked += OnMarkdownRenderLinkClicked;
        _rowNavigationPresenter = new DiffRowNavigationPresenter(
            UnifiedBox, LeftTextBox, RightTextBox, LeftGutter, RightGutter,
            () => Vm?.IsSideBySide == true,
            () => _markdownRenderController.IsActive,
            _markdownRenderController.ScrollToChange,
            () => _documentBuildController?.Flush(),
            UpdateLayout,
            () => _unifiedSv,
            () => _leftTextSv);
        _documentBuildController = new DiffDocumentBuildController(
            Dispatcher,
            () => Vm,
            _documentRenderer,
            UnifiedBox,
            LeftTextBox,
            RightTextBox,
            LeftGutter,
            RightGutter,
            () =>
            {
                _rowNavigationPresenter.ClearMarks();
                _contextRowIndex = -1;
            },
            rows =>
            {
                _sideBlockPresenter.SetRows(rows);
                _sideBlockPresenter.Render();
            });
        _autoJumpController = new DiffAutoJumpController(
            Dispatcher,
            () => Vm is not null,
            () => _documentBuildController.HasPendingBuild(Vm!.IsSideBySide),
            () => Vm?.JumpToAutoTarget());
        _scrollSyncController = new DiffScrollSyncController(
            () => LeftTextBox, () => RightTextBox,
            () => _leftGutterSv, () => _leftTextSv, () => _rightGutterSv,
            () => _rightTextSv, () => _unifiedSv, _sideBlockPresenter.Render);
        _bindingController = new DiffSessionBindingController(
            _rowNavigationPresenter.ScrollToRow, OnAutoJumpRequested, OnScrollToConflictRequested,
            (_, _) => _documentBuildController.ScheduleUnified(),
            (_, _) => _documentBuildController.ScheduleSide(),
            _markdownRenderController.SetViewModel,
            _documentBuildController.ScheduleUnified,
            _documentBuildController.ScheduleSide);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// ペインヘッダーを持たないホスト（切り離しウィンドウ＝Git のコミット詳細のダブルクリック／
    /// Diff ペインが隠れているときの差分の行き先）で、このビュー自前のツールバーを出す。ペインでは ShellWindow 側のヘッダーが同じ操作を持っているので
    /// 既定は非表示——両方出すと同じボタンが二段になる。
    /// </summary>
    public void ShowStandaloneToolbar()
    {
        StandaloneToolbar.Visibility = Visibility.Visible;
        _standalone = true;
        if (IsLoaded)
            FocusSelfForKeys();
    }

    /// <summary>切り離しホストか（＝自前ツールバーと、開いた直後のキーフォーカスを持つか）。</summary>
    private bool _standalone;

    /// <summary>開いた直後から F8／Alt+↓ が効くように、このビュー自身へキーフォーカスを置く
    /// （<c>UserControl.InputBindings</c> はフォーカスの経路に居ないと拾われない）。</summary>
    private void FocusSelfForKeys()
        => Dispatcher.BeginInvoke(new Action(() => Focus()), DispatcherPriority.Loaded);

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
        _documentBuildController.ScheduleUnified();
        _documentBuildController.ScheduleSide();
    }

    private DiffSessionViewModel? Vm => DataContext as DiffSessionViewModel;

    // ===== データ購読・FlowDocument 構築 =====

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _bindingController.SetViewModel(Vm);

    /// <summary>コンフリクトのナビゲーション（前へ/次へ・自動フォーカス）：対象の <see cref="ConflictRegionVm"/> の
    /// コンテナを見えるところまでスクロールする。ConflictBlocks はまとめて Add されるため、コンテナ生成が
    /// 完了するレイアウトパス後まで待つ（AutoJump と同じ理由）。</summary>
    private void OnScrollToConflictRequested(int regionIndex)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Vm is null) return;
            var region = DiffConflictDisplayMapper.FindRegion(Vm.Conflict.ConflictBlocks, regionIndex);
            if (region is null) return;
            if (ConflictItemsControl.ItemContainerGenerator.ContainerFromItem(region) is FrameworkElement fe)
                fe.BringIntoView();
        }), DispatcherPriority.ContextIdle);
    }

    // ファイルを開く／表示形式を切り替えたら、差分が出来てから最初の変更へ自動ジャンプする
    private void OnAutoJumpRequested() => _autoJumpController.Request();

    /// <summary>
    /// 自分で読み始めた人を後から引きずらないよう、待っている自動ジャンプを取り下げる。
    /// 分割構築の間ペインは<b>操作できる</b>ので、大きい差分では「開いた人がスクロールして読み始めた
    /// 数秒後に、最後のスライスが載った瞬間へ最初の変更へ飛ばされる」が起こり得る。触った時点で
    /// 行き先はその人が決めている。
    /// </summary>
    private void CancelAutoJump() => _autoJumpController.Cancel();

    /// <summary>既存テスト用の API。構文 Run の生成は renderer へ委譲する。</summary>
    internal static List<Run> SyntaxRuns(
        string text, SyntaxToken[] tokens, Func<TokenKind, Brush?>? foreground = null)
        => DiffFlowDocumentRenderer.SyntaxRuns(text, tokens, foreground);

    // ===== スクロール連動（左右本文＋行番号ガター） =====

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookSyntaxColors();
        _markdownRenderController.ReattachIfMoved();
        if (_standalone)
            FocusSelfForKeys();
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
        CenterGutter.SizeChanged += (_, _) => _sideBlockPresenter.Render();

        // 分割構築の途中でも本文は操作できるので、自分で読み始めた合図があれば自動ジャンプは取り下げる。
        foreach (var box in new[] { UnifiedBox, LeftTextBox, RightTextBox })
        {
            box.PreviewMouseWheel += (_, _) => CancelAutoJump();
            box.PreviewMouseDown += (_, _) => CancelAutoJump();
            box.PreviewKeyDown += (_, _) => CancelAutoJump();
        }

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
        => _scrollSyncController.ScrollHorizontally(sender, e);

    private static ScrollViewer? InnerScrollViewer(RichTextBox box)
    {
        box.ApplyTemplate();
        return box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
    }

    private void OnSideScrollChanged(object sender, ScrollChangedEventArgs e)
        => _scrollSyncController.OnSideScrollChanged(sender, e);

    internal static double ClampToSharedHorizontalRange(
        double requestedOffset,
        double leftScrollableWidth,
        double rightScrollableWidth)
        => DiffRowLineMapper.ClampToSharedHorizontalRange(
            requestedOffset, leftScrollableWidth, rightScrollableWidth);

    // ===== 次/前の変更へジャンプ =====

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
        _contextRowIndex = DiffRowLineMapper.IndexOfBlock(box.Document, paragraph);
    }

    /// <summary>
    /// キーボードで開いたメニュー（マウス座標が無い＝<c>CursorLeft/Top</c> が負）は、直前の右クリックで
    /// 覚えた行がそのまま残っていると別ファイルの行を指しかねない。キャレットのある行に取り直す。
    /// </summary>
    private void OnBodyContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.CursorLeft >= 0 || e.CursorTop >= 0) return;   // マウスで開いた（上で取得済み）
        _contextRowIndex = sender is RichTextBox box && box.CaretPosition.Paragraph is { } paragraph
            ? DiffRowLineMapper.IndexOfBlock(box.Document, paragraph)
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
        var result = DiffSelectionComparisonMapper.FromClipboard(selected, ClipboardText.TryGet());
        if (result.ErrorMessage is { } error)
        {
            vm.SetStatusMessage(error, isError: true);
            return;
        }
        vm.ShowComparison(result.Comparison!);
    }

    private void OnSwapComparison(object sender, RoutedEventArgs e) => Vm?.SwapComparisonCommand.Execute(null);

    private void OnRecompareWithClipboard(object sender, RoutedEventArgs e)
        => Vm?.RecompareWithClipboardCommand.Execute(null);

    // ===== 選択行の破棄（統合表示） =====

    /// <summary>統合表示で選択している行の変更だけを破棄する。段落の並びは <see cref="DiffSessionViewModel.DiffRows"/> と1対1。</summary>
    private async void OnDiscardSelectedLines(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var rows = DiffRowLineMapper.SelectedRowIndices(UnifiedBox.Document, UnifiedBox.Selection);
        if (rows.Count == 0) return;
        await vm.DiscardSelectedLinesAsync(rows);
    }

    /// <summary>本文の選択範囲が覆う段落（＝差分行）の添字集合を返す。キャレットだけのときはその1行。</summary>
}
