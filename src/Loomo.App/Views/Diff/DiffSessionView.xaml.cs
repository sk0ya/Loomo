using Editor.Controls;
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
/// 統合表示は読み取り専用 RichTextBox（FlowDocument）で描き、<see cref="DiffSessionViewModel.DiffRows"/> が
/// 変わるたびに組み直す。左右並びは左右2つのエディタ（<see cref="DiffSideEditorPresenter"/>）で出し、
/// ヤンク・検索などのエディタ操作がそのまま効く。右が作業ツリーのファイルなら、その場で編集して保存できる。
/// </summary>
public partial class DiffSessionView : UserControl, IDisposable
{
    private readonly DiffMarkdownRenderController _markdownRenderController;
    private readonly DiffFlowDocumentRenderer _documentRenderer;
    private readonly DiffDocumentBuildController _documentBuildController;
    private readonly DiffSideBlockPresenter _sideBlockPresenter;
    private readonly DiffRowNavigationPresenter _rowNavigationPresenter;
    private readonly DiffAutoJumpController _autoJumpController;
    private readonly DiffSessionBindingController _bindingController;
    private readonly DiffSideEditorPresenter _sideEditors;

    private ScrollViewer? _unifiedSv;
    /// <summary>左右並びの組み直しが予約済みか（行の差し替えは Clear＋Add の連発で届くので、1回にまとめる）。</summary>
    private bool _sideDirty;
    /// <summary><see cref="DiffSessionViewModel.SideRowsItem"/> の変化を見ている VM。</summary>
    private DiffSessionViewModel? _sideItemSource;
    private bool _viewHooked;   // 子コントロールへの購読済みフラグ（Loaded は再ペアレントで再入する）

    public DiffSessionView()
    {
        InitializeComponent();
        _documentRenderer = new DiffFlowDocumentRenderer(this);
        _sideEditors = new DiffSideEditorPresenter(() => Vm, LeftEditorHost, RightEditorHost, SideStatusBar);
        _sideBlockPresenter = new DiffSideBlockPresenter(
            CenterGutter,
            () => Vm,
            () => _sideEditors.Geometry,
            () => CenterGutter.ActualHeight,
            () => CenterGutter.ActualWidth > 0 ? CenterGutter.ActualWidth : 36,
            () => _sideEditors.HasUnsavedEdits);
        _sideEditors.LayoutChanged += _sideBlockPresenter.Render;
        _sideEditors.UserInteracted += CancelAutoJump;
        _sideEditors.ContextMenuBuilding += OnSideEditorContextMenuBuilding;
        _markdownRenderController = new DiffMarkdownRenderController(MarkdownRenderHost, Dispatcher);
        _markdownRenderController.LinkClicked += OnMarkdownRenderLinkClicked;
        _rowNavigationPresenter = new DiffRowNavigationPresenter(
            UnifiedBox,
            index =>
            {
                FlushSide();
                _sideEditors.ScrollToRow(index);
            },
            () => Vm?.IsSideBySide == true,
            () => _markdownRenderController.IsActive,
            _markdownRenderController.ScrollToChange,
            () => _documentBuildController?.Flush(),
            UpdateLayout,
            () => _unifiedSv);
        _documentBuildController = new DiffDocumentBuildController(
            Dispatcher,
            () => Vm,
            _documentRenderer,
            UnifiedBox,
            () =>
            {
                _rowNavigationPresenter.ClearMarks();
                _contextRowIndex = -1;
            });
        _autoJumpController = new DiffAutoJumpController(
            Dispatcher,
            () => Vm is not null,
            () => Vm!.IsSideBySide ? _sideDirty : _documentBuildController.HasPendingBuild,
            () => Vm?.JumpToAutoTarget());
        _bindingController = new DiffSessionBindingController(
            _rowNavigationPresenter.ScrollToRow, OnAutoJumpRequested, OnScrollToConflictRequested,
            (_, _) => _documentBuildController.ScheduleUnified(),
            (_, _) => ScheduleSide(),
            OnViewModelChanged,
            _documentBuildController.ScheduleUnified,
            ScheduleSide);
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
        // 左右のエディタはエディタペインと同じ当て方（テーマ・フォント）で塗り直す。
        _sideEditors.ReapplyAppearance();
    }

    /// <summary>
    /// 左右並びのエディタの作り方と見た目の当て方を部屋から受け取る（エディタペインと同じ構文の登録・
    /// テーマ・フォント・Vim の有無）。Diff ペインは XAML から生えて DI が届かないので、ホストが渡す。
    /// 渡されなければ既定のエディタで動く。<paramref name="documents"/> は右で編集するファイルの持ち主
    /// （Editor ペイン）——右はそのタブの本文を映して一緒に編集する。
    /// </summary>
    internal void ConfigureEditors(
        Func<VimEditorControl> factory, Action<VimEditorControl> applyAppearance, IDiffWorkingDocuments documents)
        => _sideEditors.Configure(factory, applyAppearance, documents);

    // ===== 左右並び（エディタ2つ） =====

    private void OnViewModelChanged(DiffSessionViewModel? viewModel)
    {
        _markdownRenderController.SetViewModel(viewModel);
        if (_sideItemSource is not null)
            _sideItemSource.PropertyChanged -= OnViewModelPropertyChanged;
        _sideItemSource = viewModel;
        if (viewModel is not null)
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 行が同じでも出どころのファイルが変われば、右で編集させるファイル・見出しが変わる。
        // 左右並びへ切り替えた瞬間も、隠れていた間に届いた行へ合わせ直す。
        if (e.PropertyName is nameof(DiffSessionViewModel.SideRowsItem) or nameof(DiffSessionViewModel.ShowSideText))
            ScheduleSide();
    }

    private void ScheduleSide()
    {
        if (_sideDirty) return;
        _sideDirty = true;
        Dispatcher.BeginInvoke(new Action(FlushSide), DispatcherPriority.Background);
    }

    /// <summary>予約済みの左右の組み直しを今やる（行の添字で動く操作の前）。</summary>
    private void FlushSide()
    {
        if (!_sideDirty) return;
        _sideDirty = false;
        if (Vm is not { } vm || !vm.ShowSideText)
            return;   // 隠れている間は組まない。見えるようになったら ShowSideText の通知で組む。
        var rows = vm.SideRows.ToList();
        ShowSideMessage(rows);
        _sideEditors.Sync();
        _sideBlockPresenter.SetRows(rows);
        _sideBlockPresenter.Render();
    }

    /// <summary>行が「差分はありません」のような知らせだけなら、エディタの上に文面を出す。</summary>
    private void ShowSideMessage(IReadOnlyList<DiffSideRowVm> rows)
    {
        var messageOnly = rows.Count > 0 && rows.All(row => row.LeftLine.Length == 0 && row.RightLine.Length == 0);
        SideMessageText.Text = messageOnly ? string.Join(Environment.NewLine, rows.Select(row => row.LeftText)) : "";
        SideMessagePanel.Visibility = messageOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>右が編集できるときの Ctrl+S（エディタペインの保存は部屋のキー割り当てが持っていて、ここまで届かない）。</summary>
    private void OnSidePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (_sideEditors.Right is not { IsKeyboardFocusWithin: true }) return;
        if (_sideEditors.SaveRight())
            e.Handled = true;
    }

    /// <summary>左右のエディタの右クリックメニューから差分に要らない項目を外し、差分ならではの項目を足す
    /// （エディタ自身の項目の後ろ）。</summary>
    private void OnSideEditorContextMenuBuilding(bool left, VimEditorControl editor, EditorContextMenuBuildingEventArgs e)
    {
        EditorNativeMenuCoordinator.RemoveByHeader(e.Menu, EditorNativeMenuCoordinator.DiffDroppedHeaders(editor.IsReadOnly));
        if (Vm is not { } vm) return;
        var row = _sideEditors.RowAtCaret(editor);
        var selected = e.SelectedText;
        e.Menu.Items.Add(new Separator());
        AddSideLineActions(vm, editor, e.Menu);
        e.Menu.Items.Add(NewMenuItem("この行をエディタで開く", "右クリックした行に対応するファイルの行をエディタで開く",
            () => vm.RequestOpenRowInEditor(row)));
        // 選んでいなければ比べる本文が無い。
        if (e.HasSelection && !string.IsNullOrEmpty(selected))
            e.Menu.Items.Add(NewMenuItem("選択範囲をクリップボードと比較", "選択したテキストとクリップボードの内容を比較する",
                () => CompareWithClipboard(vm, selected)));
        if (!vm.HasComparison) return;
        e.Menu.Items.Add(new Separator());
        e.Menu.Items.Add(NewMenuItem("⇄ 左右を入れ替える", null, () => vm.SwapComparisonCommand.Execute(null)));
        e.Menu.Items.Add(NewMenuItem("クリップボードで再比較", "右側を今のクリップボードの内容に差し替えて比較し直す",
            () => vm.RecompareWithClipboardCommand.Execute(null)));

        static MenuItem NewMenuItem(string header, string? toolTip, Action action)
        {
            var item = new MenuItem { Header = header, ToolTip = toolTip };
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>
    /// 選んだ行（選択が無ければキャレットのある変更）のステージ／アンステージと破棄。左右どちらのエディタでも
    /// 出す——読んでいるその場で選んで右クリックするのが一番短い。出す項目は行の印で決める：未ステージの行が
    /// あれば「ステージ」と「破棄」、ステージ済みの行があれば「アンステージ」。変更に掛かっていなければ出さない。
    /// </summary>
    private void AddSideLineActions(DiffSessionViewModel vm, VimEditorControl editor, ContextMenu menu)
    {
        if (!vm.CanStageLines) return;
        var (startRow, endRow, hasSelection) = _sideEditors.SelectedRows(editor);
        var selection = DiffSideBlockMapper.CollectSelectedChanges(vm.SideRows, startRow, endRow, wholeBlock: !hasSelection);
        if (selection.IsEmpty) return;

        // 見えている行番号は保存前の本文のもの、パッチはディスクの行番号で作る——混ぜると別の行に効く。
        var blocked = _sideEditors.HasUnsavedEdits;
        AddLineActionItems(menu, hasSelection, selection.HasUnstaged, selection.HasStaged, vm.CanDiscardLines, blocked,
            stage => _ = vm.StageLinesAsync(selection.OldLines, selection.NewLines, stage),
            () => _ = vm.DiscardLinesAsync(selection.OldLines, selection.NewLines));
        menu.Items.Add(new Separator());
    }

    /// <summary>ステージ／アンステージ／破棄の項目を足す（左右並び・統合表示で同じ見出しと並び）。</summary>
    private static void AddLineActionItems(
        ItemsControl menu, bool hasSelection, bool hasUnstaged, bool hasStaged, bool canDiscard, bool blocked,
        Action<bool> stage, Action discard)
    {
        var target = hasSelection ? "選択した行" : "この変更";
        if (hasUnstaged)
            menu.Items.Add(Item($"{target}をステージ", () => stage(true)));
        if (hasStaged)
            menu.Items.Add(Item($"{target}をアンステージ", () => stage(false)));
        if (hasUnstaged && canDiscard)
            menu.Items.Add(Item($"{target}を元に戻す", discard));

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem
            {
                Header = header, IsEnabled = !blocked,
                ToolTip = blocked ? "右側に保存していない編集があります。保存してから操作してください。" : null,
            };
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) => action();
            return item;
        }
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
        CenterGutter.SizeChanged += (_, _) => _sideBlockPresenter.Render();
        SidePanel.PreviewKeyDown += OnSidePreviewKeyDown;

        // 分割構築の途中でも本文は操作できるので、自分で読み始めた合図があれば自動ジャンプは取り下げる。
        // （左右のエディタは DiffSideEditorPresenter.UserInteracted で同じことをする）
        UnifiedBox.PreviewMouseWheel += (_, _) => CancelAutoJump();
        UnifiedBox.PreviewMouseDown += (_, _) => CancelAutoJump();
        UnifiedBox.PreviewKeyDown += (_, _) => CancelAutoJump();

        // Shift+ホイールで横スクロール（FlowDocumentScrollViewer は既定で横ホイールを扱わない）
        UnifiedBox.PreviewMouseWheel += OnTextPreviewMouseWheel;

        // 右クリックした「行」を覚える（メニューを開くとキャレット位置には頼れないため、
        // 押した座標から段落を引く）。「この行をエディタで開く」の対象になる。
        UnifiedBox.PreviewMouseRightButtonDown += OnBodyRightButtonDown;
        // キーボード（メニューキー／Shift+F10）で開いたときはマウス座標が無いのでキャレット行を対象にする。
        UnifiedBox.ContextMenuOpening += OnBodyContextMenuOpening;
    }

    /// <summary>Shift 押下中のホイールを統合表示の横スクロールに割り当てる。</summary>
    private void OnTextPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || _unifiedSv is null) return;
        _unifiedSv.ScrollToHorizontalOffset(_unifiedSv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? InnerScrollViewer(RichTextBox box)
    {
        box.ApplyTemplate();
        return box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
    }

    // ===== 次/前の変更へジャンプ =====

    /// <summary>コンフリクトの Result 欄をクリック/フォーカスしたら、そのコンフリクトを「現在地」にする
    /// （ツールバーの Ours/両方/Theirs/適用 ボタンの対象を合わせる）。</summary>
    private void OnConflictResultGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConflictRegionVm region } && Vm is not null)
            Vm.Conflict.FocusConflictRegion(region);
    }

    // ===== ファイル一覧 =====

    /// <summary>ファイル行ダブルクリック：エディタで開く。フォルダ行のダブルクリックは開閉だけ
    /// （選択中のファイルを開くと、押した行と違う物が開く）。</summary>
    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm
            && FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { DataContext: DiffFileTreeNode { File: { } file } })
            vm.OpenInEditorCommand.Execute(file);
    }

    /// <summary>行の選択を VM の選択（正本）へ写す。フォルダ行を選んでも差分の表示は変えない。</summary>
    private void OnFileTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is { } vm && e.NewValue is DiffFileTreeNode { File: { } file })
            vm.SelectedFile = file;
    }

    /// <summary>VM 側から選択が移ったとき（次／前のファイル・ほかのペインから開く）も、その行を見える位置へ。</summary>
    private void OnFileTreeItemSelected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item)
            item.BringIntoView();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    /// <summary>右クリックしたメニュー項目が属する行のファイル項目（一覧の行メニュー用）。</summary>
    private static DiffFileItem? ContextItem(object sender)
        => sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: DiffFileTreeNode { File: { } item } } } }
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
        CompareWithClipboard(vm, box?.Selection.Text ?? "");
    }

    private static void CompareWithClipboard(DiffSessionViewModel vm, string selected)
    {
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

    // ===== 選択行のステージ／アンステージ／破棄（統合表示） =====

    /// <summary>統合表示で選択している行の変更だけをステージ（ステージ済みの差分ならアンステージ）する。</summary>
    /// <summary>
    /// 統合表示のメニューを開くとき、選んでいる行（選択が無ければキャレットの行）の印に合わせて
    /// ステージ／アンステージ／破棄の項目を差し込む。項目は静的に置かず毎回作る——出すべきものが選択で変わる。
    /// </summary>
    private void OnUnifiedMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var stale in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, LineActionTag)).ToList())
            menu.Items.Remove(stale);
        if (Vm is not { CanStageLines: true } vm) return;

        var rows = DiffRowLineMapper.SelectedRowIndices(UnifiedBox.Document, UnifiedBox.Selection);
        var changed = rows.Where(i => i >= 0 && i < vm.DiffRows.Count && vm.DiffRows[i].Kind is "Added" or "Removed").ToList();
        if (changed.Count == 0) return;
        var hasStaged = changed.Any(i => vm.DiffRows[i].Staged);
        var hasUnstaged = changed.Any(i => !vm.DiffRows[i].Staged);
        var selected = changed.ToHashSet();

        var temp = new ContextMenu();
        AddLineActionItems(temp, hasSelection: !UnifiedBox.Selection.IsEmpty, hasUnstaged, hasStaged, vm.CanDiscardLines,
            blocked: false,
            stage => _ = StageUnifiedAsync(vm, selected, stage),
            () => _ = DiscardUnifiedAsync(vm, selected));
        temp.Items.Add(new Separator());
        // 「コピー」などの後ろ、エディタへ素材を渡す項目の前に差し込む。
        var insertAt = Math.Min(2, menu.Items.Count);
        foreach (var item in temp.Items.OfType<FrameworkElement>().ToList())
        {
            temp.Items.Remove(item);
            item.Tag = LineActionTag;
            menu.Items.Insert(insertAt++, item);
        }
    }

    private const string LineActionTag = "diff-line-action";

    private static async Task StageUnifiedAsync(DiffSessionViewModel vm, IReadOnlySet<int> rows, bool stage)
    {
        var (head, worktree) = await vm.UnifiedRowsToLinesAsync(rows);
        await vm.StageLinesAsync(head, worktree, stage);
    }

    private static async Task DiscardUnifiedAsync(DiffSessionViewModel vm, IReadOnlySet<int> rows)
    {
        var (head, worktree) = await vm.UnifiedRowsToLinesAsync(rows);
        await vm.DiscardLinesAsync(head, worktree);
    }


    /// <summary>本文の選択範囲が覆う段落（＝差分行）の添字集合を返す。キャレットだけのときはその1行。</summary>
}
