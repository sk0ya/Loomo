using System.Collections.Specialized;

using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの1カラム。詳細リスト・並べ替え・絞り込み・複数選択・
/// ドラッグ＆ドロップ・右クリックを担う。
///
/// <para>操作の語彙（右クリックの項目名と並び、Ctrl+C/X/V/D・F2・Delete）はサイドバーのツリー
/// （<see cref="FolderTreeView"/>）と揃える——同じ操作が2箇所で違う名前・違う順に並ぶ方が事故になる。
/// 実体は ViewModel 経由で同じ <see cref="FolderTreeCommandHandler"/> に落ちる。</para></summary>
public partial class FilesColumnView : UserControl
{
    private FilesColumnViewModel? _boundVm;
    private readonly FilesColumnWidthPresenter _columnWidths;
    private readonly FilesColumnBreadcrumbPickerPresenter _breadcrumbPicker;
    private double _placesPaneWidth = 240;
    private readonly FilesColumnCommandController _fileCommands;
    private readonly FilesColumnKeyboardInteractionController _keyboardInteraction;
    private readonly FilesColumnDragDropController _dragDrop;

    public FilesColumnView()
    {
        InitializeComponent();
        _fileCommands = new FilesColumnCommandController(
            context => FileConflictDialog.Show(OwnerWindow, context));
        _keyboardInteraction = new FilesColumnKeyboardInteractionController(
            EntryList, FilterBox, () => Vm, _fileCommands, ShowProperties, RenameEntry, DeleteEntries);
        _dragDrop = new FilesColumnDragDropController(EntryList, () => Vm, Selection, _fileCommands);
        _columnWidths = new FilesColumnWidthPresenter(EntryList, this);
        _breadcrumbPicker = new FilesColumnBreadcrumbPickerPresenter(
            BreadcrumbPickerPopup, BreadcrumbPickerTree, BreadcrumbScroll, () => Vm);
        _shellInteraction = new FilesColumnShellInteractionController(
            () => Vm, Selection, () => OwnerWindow, SelectPath, ShowError, Dispatcher);
        DataContextChanged += OnDataContextChanged;
        // 閉じたカラムの裏で ZIP 生成を走らせ続けない
        // （ZIP は途中の一時ファイルもコマンド側が片付ける）。
        Unloaded += (_, _) =>
        {
            _keyboardInteraction.StopTypeAheadTimer();
            _shellInteraction.CancelPending();
            // 住所欄を開いたまま外されたら、畳んでウィンドウの見張りも外す（見張りが残ると
            // 閉じたカラムがウィンドウのクリックを掴み続ける）。
            Vm?.CancelAddressEdit();
            UpdateAddressSuggestionPopup();
        };
        // クリック・フォーカスのどちらでも「操作対象のカラム」になる。
        PreviewMouseDown += (_, _) => Vm?.NotifyActivated();
        PreviewGotKeyboardFocus += (_, _) => Vm?.NotifyActivated();
        // Ctrl+L はペイン全体で受ける（一覧・絞り込み欄、どこにフォーカスがあっても住所へ飛べる）。
        PreviewKeyDown += OnColumnPreviewKeyDown;
    }

    private FilesColumnViewModel? Vm => DataContext as FilesColumnViewModel;

    private Window? OwnerWindow => Window.GetWindow(this);

    private void OnColumnGripMouseDown(object sender, MouseButtonEventArgs e)
        => _columnWidths.OnColumnGripMouseDown(sender, e);

    private void OnColumnGripDragStarted(object sender, DragStartedEventArgs e)
        => _columnWidths.OnColumnGripDragStarted(sender, e);

    private void OnColumnGripDragDelta(object sender, DragDeltaEventArgs e)
        => _columnWidths.OnColumnGripDragDelta(sender, e);

    private void OnColumnGripDragCompleted(object sender, DragCompletedEventArgs e)
        => _columnWidths.OnColumnGripDragCompleted(sender, e);

    /// <summary>このカラムへフォーカスを移す（ペインのフォーカス受け口から呼ばれる）。</summary>
    public void FocusList()
        => _keyboardInteraction.FocusList();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null)
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = Vm;
        _columnWidths.Attach(_boundVm);
        if (_boundVm is not null)
            _boundVm.PropertyChanged += OnVmPropertyChanged;
    }

    // VM が「この行を選んでほしい」と言ってきたら（作成・名前変更・貼り付けの直後、Reveal）、
    // 一覧の中から探して選択＋スクロールする。行の実体化はレイアウト後なので一度譲る。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 候補は入力のたびに作り直されるので、その結果に合わせてポップアップを開閉する
        // （OnAddressTextChanged が先に走るため、ここでは新しい候補が見えている）。
        if (e.PropertyName is nameof(FilesColumnViewModel.AddressText)
            or nameof(FilesColumnViewModel.AddressError)
            or nameof(FilesColumnViewModel.IsAddressEditing))
        {
            UpdateAddressSuggestionPopup();
            return;
        }

        // 一覧の形が変わると列見出しの出入りも変わる。自動のあいだは出した時点で配り直す。
        if (e.PropertyName == nameof(FilesColumnViewModel.DisplayMode))
        {
            _columnWidths.OnDisplayModeChanged();
            return;
        }

        if (e.PropertyName == nameof(FilesColumnViewModel.CurrentFolder))
        {
            _columnWidths.OnFolderChanged();
            return;
        }

        if (e.PropertyName != nameof(FilesColumnViewModel.PendingSelection) || Vm?.PendingSelection is not { } path)
            return;
        Vm.PendingSelection = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SelectPath(path)));
    }

    private void SelectPath(string fullPath)
    {
        if (Vm is null)
            return;
        var target = Vm.Entries.FirstOrDefault(
            entry => FilePathRelations.AreEqual(entry.FullPath, fullPath));
        if (target is null)
            return;
        EntryList.SelectedItems.Clear();
        EntryList.SelectedItem = target;
        EntryList.ScrollIntoView(target);
    }

    // ===== 場所（ワークスペース・ピン留め・クイックアクセス・PC） =====

    private void OnPlacesExpanded(object sender, RoutedEventArgs e)
    {
        if (PlacesColumn.Width.Value < 1)
            PlacesColumn.Width = new GridLength(Math.Clamp(_placesPaneWidth, 180, 420));
        PlacesSplitter.Visibility = Visibility.Visible;
        Vm?.SetPlacesOpen(true);
    }

    private void OnPlacesCollapsed(object sender, RoutedEventArgs e)
    {
        if (PlacesColumn.ActualWidth >= 1)
            _placesPaneWidth = Math.Clamp(PlacesColumn.ActualWidth, 180, 420);
        PlacesSplitter.Visibility = Visibility.Collapsed;
        PlacesColumn.Width = new GridLength(0);
        Vm?.SetPlacesOpen(false);
    }

    private void OnBreadcrumbPickerMouseDown(object sender, MouseButtonEventArgs e)
        => _breadcrumbPicker.OnPickerMouseDown();

    private void OnBreadcrumbPickerMouseUp(object sender, MouseButtonEventArgs e)
        => _breadcrumbPicker.OnPickerMouseUp(e);

    private void OnBreadcrumbScrollChanged(object sender, ScrollChangedEventArgs e)
        => _breadcrumbPicker.OnBreadcrumbScrollChanged(e);

    private void OnBreadcrumbPickerClick(object sender, RoutedEventArgs e)
        => _breadcrumbPicker.OnPickerClick(sender, e);

    private void OnBreadcrumbPickerItemExpanded(object sender, RoutedEventArgs e)
        => _breadcrumbPicker.OnItemExpanded(sender, e);

    private void OnBreadcrumbPickerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _breadcrumbPicker.OnPreviewMouseLeftButtonDown(sender, e);

    /// <summary>場所は常設の縦パネルであってポップアップではないので、項目を開いても畳まない。
    /// 閉じるのはツールバーの「場所」ボタンを押したときだけにする（続けて別の場所へ飛べる）。</summary>
    private void OnPlaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FilesPlace place })
            Vm?.OpenPlace(place);
    }

    /// <summary>ピン留めの対象＝選んでいるフォルダー行、無ければ現在地。</summary>
    private string? PinTarget()
        => _fileCommands.PinTarget(Vm, SingleSelection());

    private void OnPinClick(object sender, RoutedEventArgs e) => Vm?.TogglePin(PinTarget());

    private void OnUnpinClick(object sender, RoutedEventArgs e) => Vm?.TogglePin(PinTarget());

    // ===== 選択・起動 =====

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is not null && EntryList.SelectedItems.Count == 1
            && EntryList.SelectedItem is FileEntryViewModel entry)
            Vm.NotifySelected(entry.FullPath);
    }

    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesColumnDragDropController.EntryAt(e.OriginalSource) is { } entry)
        {
            Vm?.OpenEntry(entry);
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragDrop.OnPreviewMouseLeftButtonDown(e);
    }

    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右クリックした行を操作対象にする。選択集合の中を右クリックしたときは集合を保つ
        // （一括操作の対象にするため）。集合の外ならその1件へ絞る（エクスプローラーと同じ）。
        if (FilesColumnDragDropController.EntryAt(e.OriginalSource) is not { } entry)
            return;
        if (!EntryList.SelectedItems.Contains(entry))
        {
            EntryList.SelectedItems.Clear();
            EntryList.SelectedItem = entry;
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
        => _keyboardInteraction.OnListPreviewKeyDown(e);

    // 一覧への直接の文字入力は Explorer と同じ type-ahead 選択にする。j/k は上の KeyDown で
    // 移動として処理済みなので、ここには通常の文字入力だけが届く。
    private void OnListPreviewTextInput(object sender, TextCompositionEventArgs e)
        => _keyboardInteraction.OnPreviewTextInput(e);

    // ===== コンテキストメニュー =====

    /// <summary>選択の中身と書き込み可否に合わせて項目を出し分ける。Tag の意味は
    /// <c>Selection</c>＝1件以上選択、<c>Single</c>＝ちょうど1件、<c>FileOnly</c>／<c>DirOnly</c>＝
    /// 1件かつファイル／フォルダー、<c>Html</c>＝1件かつ HTML、<c>CompareTwo</c>＝ファイルちょうど2件、
    /// <c>Writable*</c>＝ワークスペース配下（＝書き込める）とき、<c>SearchableDir</c>＝検索へ送れる
    /// フォルダー、<c>Pinnable</c>／<c>Unpinnable</c>＝ピン留めの可否。</summary>
    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || Vm is not { } vm)
            return;

        var selection = Selection();
        // Explorer のクイックアクセス照会は数秒かかるため、準備済みの状態だけ先に反映する。
        var menuState = _fileCommands.CreateContextMenuState(vm, selection);
        var quickAccessReady = menuState.QuickAccessReady;

        FileContextMenuPresenter.PrepareFilesColumnMenu(
            menu, menuState, vm.History.UndoDescription, vm.History.RedoDescription);
        FileContextMenuPresenter.UpdateFilesColumnQuickAccessItems(
            menu, vm, selection, _fileCommands, quickAccessReady);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
        => Vm?.OpenEntry(EntryList.SelectedItem as FileEntryViewModel);

    // ===== 絞り込み（「/」で開く下端のバー） =====

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
        => _keyboardInteraction.OnFilterKeyDown(e);

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { IsDirectory: false } entry)
            Vm?.RequestOpenInBrowser(entry.FullPath);
    }

    // 拡張子に紐づく既定のアプリで開く（PDF・画像・Office 等、エディタペインで扱えない素材の逃げ道）。
    private void OnOpenWithDefaultAppClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is not { IsDirectory: false } entry)
            return;
        FileExplorerLauncher.OpenWithDefaultApp(entry.FullPath);
    }

    private void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is not { } entry)
            return;
        FileExplorerLauncher.RevealInExplorer(entry.FullPath);
    }

    private void OnOpenInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { } entry)
            FileExplorerLauncher.OpenInExplorer(entry.FullPath);
    }

    private void OnOpenCurrentFolderInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { CurrentFolder: { Length: > 0 } folder })
            FileExplorerLauncher.OpenInExplorer(folder);
    }

    private void OnSetInTerminalClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { } entry)
            Vm?.RequestSetInTerminal(entry);
    }

    private void OnCompareWithClipboardClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { IsDirectory: false } entry)
            Vm?.RequestCompare(entry.FullPath, rightPath: null);
    }

    private void OnCompareSelectedClick(object sender, RoutedEventArgs e)
        => _fileCommands.CompareSelectedFiles(Vm, Selection());

    private void OnSearchInFolderClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { IsDirectory: true } entry)
            Vm?.RequestSearchInFolder(entry.FullPath);
    }

    private void OnFileAiClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
            return;
        var action = FileContextMenuPolicy.ResolveFileAiAction((sender as MenuItem)?.Tag as string);
        if (action is { } selectedAction)
            Vm.RequestFileAi(selectedAction, Selection());
    }

    private void OnGitBlameClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { IsDirectory: false } entry)
            Vm?.RequestGitBlame(entry);
    }

    private void OnGitHistoryClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { } entry)
            Vm?.RequestGitHistory(entry);
    }

    private void OnAddToGitignoreClick(object sender, RoutedEventArgs e)
        => _fileCommands.AddToGitignore(Vm, SingleSelection());

    private void OnNewFileClick(object sender, RoutedEventArgs e) => CreateEntry(isDirectory: false);

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => CreateEntry(isDirectory: true);

    private void CreateEntry(bool isDirectory)
    {
        _fileCommands.CreateEntry(Vm, isDirectory, requestedDirectory =>
        {
            var title = requestedDirectory ? "新規フォルダー" : "新規ファイル";
            return requestedDirectory
                ? InputDialog.Prompt(OwnerWindow, title, $"{title}名を入力:")
                : NewFileDialog.Prompt(OwnerWindow);
        });
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameEntry(SingleSelection());

    private void RenameEntry(FileEntryViewModel? entry)
        => _fileCommands.RenameEntry(Vm, entry, target => InputDialog.Prompt(
            OwnerWindow, "名前の変更", "新しい名前を入力:", target.Name,
            selectNameOnly: !target.IsDirectory));

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteEntries(Selection());

    /// <summary>選択をまとめてゴミ箱へ送る（確認は1回だけ）。</summary>
    private void DeleteEntries(IReadOnlyList<FileEntryViewModel> entries)
        => _fileCommands.DeleteEntries(Vm, entries, message =>
            MessageBox.Show(message, "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK);

    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateEntries(Selection());

    private void DuplicateEntries(IReadOnlyList<FileEntryViewModel> entries)
        => _fileCommands.DuplicateEntries(Vm, entries);

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => _fileCommands.CopyFiles(Selection(), move: false);

    private void OnCutClick(object sender, RoutedEventArgs e)
        => _fileCommands.CopyFiles(Selection(), move: true);

    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteFromClipboard();

    private void PasteFromClipboard()
        => _fileCommands.PasteFromClipboard(Vm);

    // ===== 元に戻す／やり直す（ファイル操作の Undo/Redo・ツリーと共有の履歴） =====

    private void OnUndoFileOperationClick(object sender, RoutedEventArgs e) => UndoFileOperation();

    private void OnRedoFileOperationClick(object sender, RoutedEventArgs e) => RedoFileOperation();

    private void UndoFileOperation() => RunHistoryStep(undo: true);

    private void RedoFileOperation() => RunHistoryStep(undo: false);

    private async void RunHistoryStep(bool undo)
        => await _fileCommands.RunHistoryStepAsync(Vm, undo);

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
        => _fileCommands.CopyPaths(Selection());

    private void OnCopyRelativePathClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            _fileCommands.CopyRelativePaths(vm, Selection());
    }

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
        => _fileCommands.CopyNames(Selection());

    // ===== ドラッグ＆ドロップ =====
    // カラム内・カラム間のドロップで移動、外部（エクスプローラー等）からのドロップでコピー。
    // 修飾キー: Ctrl=コピー強制 / Shift=移動強制。ツリー（FolderTreeView.DragDrop.cs）と同じ規則。

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
        => _dragDrop.OnPreviewMouseMove(e);

    private void OnListDragOver(object sender, DragEventArgs e)
        => _dragDrop.OnDragOver(e);

    private void OnListDrop(object sender, DragEventArgs e)
        => _dragDrop.OnDrop(e);

    // ===== 小物 =====

    /// <summary>選択中の行（一覧の並び順）。</summary>
    private List<FileEntryViewModel> Selection()
        => EntryList.SelectedItems.OfType<FileEntryViewModel>().ToList();

    private FileEntryViewModel? SingleSelection()
    {
        var selection = Selection();
        return selection.Count == 1 ? selection[0] : null;
    }

    private static void ShowError(string message) => ToastService.Error(message);
}
