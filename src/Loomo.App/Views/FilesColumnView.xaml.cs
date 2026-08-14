using System.Collections.Specialized;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの1カラム。詳細リスト・並べ替え・絞り込み・複数選択・
/// ドラッグ＆ドロップ・右クリックを担う。
///
/// <para>操作の語彙（右クリックの項目名と並び、Ctrl+C/X/V/D・F2・Delete）はサイドバーのツリー
/// （<see cref="FolderTreeView"/>）と揃える——同じ操作が2箇所で違う名前・違う順に並ぶ方が事故になる。
/// 実体は ViewModel 経由で同じ <see cref="FolderTreeCommandHandler"/> に落ちる。</para></summary>
public partial class FilesColumnView : UserControl
{
    private Point _dragStart;
    private FileEntryViewModel? _dragCandidate;
    // Loomo 自身が発生源のドラッグ中フラグ。カラムをまたぐドラッグ（左のカラム → 右のカラム）でも
    // 「内部＝移動」と扱うため、インスタンスではなく型で持つ（ドラッグは同時に1つしか走らない）。
    private static bool _internalDrag;
    // 単クリックのプレビュー判定：押した行と離した行が同じときだけプレビューする。
    private FileEntryViewModel? _pressedEntry;
    private FilesColumnViewModel? _boundVm;

    public FilesColumnView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // クリック・フォーカスのどちらでも「操作対象のカラム」になる。
        PreviewMouseDown += (_, _) => Vm?.NotifyActivated();
        PreviewGotKeyboardFocus += (_, _) => Vm?.NotifyActivated();
    }

    private FilesColumnViewModel? Vm => DataContext as FilesColumnViewModel;

    private Window? OwnerWindow => Window.GetWindow(this);

    /// <summary>このカラムへフォーカスを移す（ペインのフォーカス受け口から呼ばれる）。</summary>
    public void FocusList()
    {
        if (EntryList.Items.Count > 0 && EntryList.SelectedIndex < 0)
            EntryList.SelectedIndex = 0;
        var container = EntryList.ItemContainerGenerator.ContainerFromIndex(
            Math.Max(0, EntryList.SelectedIndex)) as ListBoxItem;
        if (container is not null)
            container.Focus();
        else
            EntryList.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null)
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = Vm;
        if (_boundVm is not null)
            _boundVm.PropertyChanged += OnVmPropertyChanged;
    }

    // VM が「この行を選んでほしい」と言ってきたら（作成・名前変更・貼り付けの直後、Reveal）、
    // 一覧の中から探して選択＋スクロールする。行の実体化はレイアウト後なので一度譲る。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            entry => string.Equals(entry.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return;
        EntryList.SelectedItems.Clear();
        EntryList.SelectedItem = target;
        EntryList.ScrollIntoView(target);
    }

    // ===== 場所（ワークスペース・ピン留め・クイックアクセス・PC） =====

    private void OnPlacesOpened(object sender, RoutedEventArgs e) => Vm?.LoadPlaces();

    // ポップアップが開いている間、ボタンの押下は Popup のマウスキャプチャに飲まれてボタンまで
    // 届かない（Popup は自分で閉じる）。ところがその後のマウスアップは届き、それだけでボタンが
    // Click 扱いになるため、閉じた直後にまた開く＝押しても閉じないトグルになっていた。
    // 対のダウンを受けていないアップは無視すれば、押すたびに開閉する。
    private bool _placesButtonPressed;

    private void OnPlacesButtonMouseDown(object sender, MouseButtonEventArgs e) => _placesButtonPressed = true;

    private void OnPlacesButtonMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_placesButtonPressed)
            e.Handled = true;
        _placesButtonPressed = false;
    }

    /// <summary>パンくずが幅に収まらないときは末尾（現在地）を見せる。左端から切ると、
    /// 狭いカラムで「今どこにいるか」だけが消えることになる。</summary>
    private void OnBreadcrumbScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)
            BreadcrumbScroll.ScrollToRightEnd();
    }

    private void OnPlaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
            Vm?.Navigate(path);
        PlacesButton.IsChecked = false;
    }

    /// <summary>ピン留めの対象＝選んでいるフォルダー行、無ければ現在地。</summary>
    private string? PinTarget()
        => SingleSelection() is { IsDirectory: true } entry ? entry.FullPath : Vm?.CurrentFolder;

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
        if (EntryAt(e.OriginalSource) is { } entry)
        {
            Vm?.OpenEntry(entry);
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = EntryAt(e.OriginalSource);
        _pressedEntry = _dragCandidate;
    }

    // 単クリックはプレビュータブで開く（ツリーと同じ扱い。編集するまで確定しない1枚を使い回す）。
    // 修飾キー付き＝複数選択の操作なので開かない。
    private void OnListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var entry = EntryAt(e.OriginalSource);
        var pressed = _pressedEntry;
        _pressedEntry = null;
        if (entry is null || !ReferenceEquals(entry, pressed) || entry.IsDirectory)
            return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            return;
        Vm?.NotifyPreviewRequested(entry.FullPath);
    }

    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右クリックした行を操作対象にする。選択集合の中を右クリックしたときは集合を保つ
        // （一括操作の対象にするため）。集合の外ならその1件へ絞る（エクスプローラーと同じ）。
        if (EntryAt(e.OriginalSource) is not { } entry)
            return;
        if (!EntryList.SelectedItems.Contains(entry))
        {
            EntryList.SelectedItems.Clear();
            EntryList.SelectedItem = entry;
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
            return;

        // Alt+←／→ は戻る／進む（Alt 付きは SystemKey に入る）。
        if ((e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0)
        {
            var systemKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (systemKey == Key.Left && Vm.GoBackCommand.CanExecute(null))
            {
                Vm.GoBackCommand.Execute(null);
                e.Handled = true;
            }
            else if (systemKey == Key.Right && Vm.GoForwardCommand.CanExecute(null))
            {
                Vm.GoForwardCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if ((e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.C:
                    FileClipboard.SetFiles(Selection().Select(entry => entry.FullPath), move: false);
                    e.Handled = true;
                    return;
                case Key.X:
                    FileClipboard.SetFiles(Selection().Select(entry => entry.FullPath), move: true);
                    e.Handled = true;
                    return;
                case Key.V:
                    PasteFromClipboard();
                    e.Handled = true;
                    return;
                case Key.D:
                    DuplicateEntries(Selection());
                    e.Handled = true;
                    return;
            }
            return;
        }

        switch (e.Key)
        {
            // 「/」で絞り込みバーを開く（エディタの検索と同じ入り方）。
            case Key.OemQuestion or Key.Divide:
                OpenFilter();
                e.Handled = true;
                break;
            case Key.Escape when Vm.IsFilterBarOpen:
                Vm.CloseFilter();
                e.Handled = true;
                break;
            case Key.Enter:
                Vm.OpenEntry(EntryList.SelectedItem as FileEntryViewModel);
                e.Handled = true;
                break;
            case Key.Back:
                if (Vm.GoUpCommand.CanExecute(null))
                    Vm.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                RenameEntry(SingleSelection());
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteEntries(Selection());
                e.Handled = true;
                break;
            case Key.F5:
                Vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ===== コンテキストメニュー =====

    /// <summary>選択の中身と書き込み可否に合わせて項目を出し分ける。Tag の意味は
    /// <c>Selection</c>＝1件以上選択、<c>Single</c>＝ちょうど1件、<c>FileOnly</c>／<c>DirOnly</c>＝
    /// 1件かつファイル／フォルダー、<c>Html</c>＝1件かつ HTML、<c>CompareTwo</c>＝ファイルちょうど2件、
    /// <c>Writable*</c>＝ワークスペース配下（＝書き込める）とき、<c>SearchableDir</c>＝検索へ送れる
    /// フォルダー、<c>Pinnable</c>／<c>Unpinnable</c>＝ピン留めの可否。</summary>
    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || Vm is null)
            return;

        var selection = Selection();
        var single = selection.Count == 1 ? selection[0] : null;
        var files = selection.Where(entry => !entry.IsDirectory).ToList();
        var pinTarget = PinTarget();

        foreach (var item in Descendants(menu))
        {
            var visible = (item.Tag as string) switch
            {
                "Selection" => selection.Count > 0,
                "Single" => single is not null,
                "FileOnly" => single is { IsDirectory: false },
                "DirOnly" => single is { IsDirectory: true },
                "Html" => single is { IsHtml: true },
                "CompareTwo" => files.Count == 2,
                "SearchableDir" => single is { IsDirectory: true } && Vm.CanSearchIn(single.FullPath),
                "Pinnable" => Vm.CanPin(pinTarget),
                "Unpinnable" => Vm.IsPinned(pinTarget),
                _ => true,
            };
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        FolderTreeView.NormalizeSeparators(menu);
        foreach (var submenu in menu.Items.OfType<MenuItem>())
            FolderTreeView.NormalizeSeparators(submenu);
    }

    private static IEnumerable<MenuItem> Descendants(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Descendants(item))
                yield return child;
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
        => Vm?.OpenEntry(EntryList.SelectedItem as FileEntryViewModel);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Vm?.RefreshCommand.Execute(null);

    // ===== 絞り込み（「/」で開く下端のバー） =====

    private void OpenFilter()
    {
        if (Vm is null)
            return;
        Vm.IsFilterBarOpen = true;
        // 出したばかりのバーはまだ配置されていないので、レイアウト後にフォーカスする。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }));
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                Vm.CloseFilter();
                FocusList();
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Down:
                // 絞り込みは効かせたまま一覧へ戻る（バーは開いたまま＝効いていることが見える）。
                FocusList();
                e.Handled = true;
                break;
        }
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { IsDirectory: false } entry)
            Vm?.RequestOpenInBrowser(entry.FullPath);
    }

    // 拡張子に紐づく既定のアプリで開く（PDF・画像・Office 等、エディタペインで扱えない素材の逃げ道）。
    private void OnOpenWithDefaultAppClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is not { IsDirectory: false } entry || !File.Exists(entry.FullPath))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"既定のアプリで開けませんでした: {ex.Message}");
        }
    }

    private void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is not { } entry)
            return;
        try
        {
            if (File.Exists(entry.FullPath))
                Process.Start("explorer.exe", $"/select,\"{entry.FullPath}\"");
            else if (Directory.Exists(entry.FullPath))
                Process.Start("explorer.exe", $"\"{entry.FullPath}\"");
        }
        catch { /* explorer 起動失敗は無視 */ }
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
    {
        var files = Selection().Where(entry => !entry.IsDirectory).ToList();
        if (files.Count == 2)
            Vm?.RequestCompare(files[0].FullPath, files[1].FullPath);
    }

    private void OnSearchInFolderClick(object sender, RoutedEventArgs e)
    {
        if (SingleSelection() is { IsDirectory: true } entry)
            Vm?.RequestSearchInFolder(entry.FullPath);
    }

    private void OnNewFileClick(object sender, RoutedEventArgs e) => CreateEntry(isDirectory: false);

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => CreateEntry(isDirectory: true);

    private void CreateEntry(bool isDirectory)
    {
        if (Vm is not { TargetDirectory: not null })
            return;
        var title = isDirectory ? "新規フォルダー" : "新規ファイル";
        var name = InputDialog.Prompt(OwnerWindow, title, $"{title}名を入力:");
        if (name is null)
            return;
        try
        {
            var created = Vm.CreateEntry(name, isDirectory);
            if (!isDirectory)
                Vm.OpenEntry(Vm.Entries.FirstOrDefault(
                    entry => string.Equals(entry.FullPath, created, StringComparison.OrdinalIgnoreCase)));
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameEntry(SingleSelection());

    private void RenameEntry(FileEntryViewModel? entry)
    {
        if (entry is null || Vm is null)
            return;
        var newName = InputDialog.Prompt(
            OwnerWindow, "名前の変更", "新しい名前を入力:", entry.Name, selectNameOnly: !entry.IsDirectory);
        if (newName is null)
            return;
        try { Vm.RenameEntry(entry, newName); }
        catch (InvalidOperationException ex) { ShowError(ex.Message); }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteEntries(Selection());

    /// <summary>選択をまとめてゴミ箱へ送る（確認は1回だけ）。</summary>
    private void DeleteEntries(IReadOnlyList<FileEntryViewModel> entries)
    {
        if (entries.Count == 0 || Vm is null)
            return;
        var message = entries.Count == 1
            ? $"{(entries[0].IsDirectory ? "フォルダー" : "ファイル")}「{entries[0].Name}」をゴミ箱へ移動しますか？"
            : $"選択した {entries.Count} 件をゴミ箱へ移動しますか？";
        if (MessageBox.Show(message, "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        foreach (var entry in entries)
        {
            try { Vm.DeleteEntry(entry); }
            catch (InvalidOperationException ex) { ShowError(ex.Message); }
        }
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateEntries(Selection());

    private void DuplicateEntries(IReadOnlyList<FileEntryViewModel> entries)
    {
        if (Vm is null)
            return;
        foreach (var entry in entries)
        {
            try { Vm.DuplicateEntry(entry); }
            catch (InvalidOperationException ex) { ShowError(ex.Message); }
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => FileClipboard.SetFiles(Selection().Select(entry => entry.FullPath), move: false);

    private void OnCutClick(object sender, RoutedEventArgs e)
        => FileClipboard.SetFiles(Selection().Select(entry => entry.FullPath), move: true);

    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteFromClipboard();

    private void PasteFromClipboard()
    {
        if (Vm is not { TargetDirectory: { } target } || !FileClipboard.ContainsFiles())
            return;

        var move = FileClipboard.PrefersMove();
        try
        {
            foreach (var source in FileClipboard.GetFiles())
                Vm.PasteEntry(target, source, move);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        // 切り取り→貼り付け（移動）はエクスプローラー同様、成功後にクリップボードを空にする。
        if (move)
            FileClipboard.Clear();
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
        => FileClipboard.CopyLines(Selection().Select(entry => entry.FullPath));

    private void OnCopyRelativePathClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            FileClipboard.CopyLines(Selection().Select(vm.RelativePathFor));
    }

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
        => FileClipboard.CopyLines(Selection().Select(entry => entry.Name));

    // ===== ドラッグ＆ドロップ =====
    // カラム内・カラム間のドロップで移動、外部（エクスプローラー等）からのドロップでコピー。
    // 修飾キー: Ctrl=コピー強制 / Shift=移動強制。ツリー（FolderTreeView.DragDrop.cs）と同じ規則。

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null)
            return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var origin = _dragCandidate;
        _dragCandidate = null;
        _pressedEntry = null;

        // 掴んだ行が選択に含まれていれば選択ぜんぶ、含まれなければその1件だけを運ぶ。
        var sources = EntryList.SelectedItems.Contains(origin)
            ? Selection()
            : new List<FileEntryViewModel> { origin };
        var paths = sources
            .Where(entry => File.Exists(entry.FullPath) || Directory.Exists(entry.FullPath))
            .Select(entry => entry.FullPath)
            .ToList();
        if (paths.Count == 0)
            return;

        var data = new DataObject();
        var list = new StringCollection();
        foreach (var path in paths)
            list.Add(path);
        data.SetFileDropList(list);

        _internalDrag = true;
        try { DragDrop.DoDragDrop(EntryList, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch { /* ドラッグ中の例外は無視 */ }
        finally { _internalDrag = false; }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveDropEffect(e, out _);
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        var effect = ResolveDropEffect(e, out var targetDirectory);
        e.Handled = true;
        if (effect == DragDropEffects.None || targetDirectory is null || Vm is null
            || e.Data.GetData(DataFormats.FileDrop) is not string[] sources)
            return;

        var move = (effect & DragDropEffects.Move) != 0;
        try
        {
            foreach (var source in sources)
                if (!string.IsNullOrEmpty(source))
                    Vm.PasteEntry(targetDirectory, source, move);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private DragDropEffects ResolveDropEffect(DragEventArgs e, out string? targetDirectory)
    {
        targetDirectory = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || Vm is null)
            return DragDropEffects.None;

        targetDirectory = Vm.DropTargetFor(EntryAt(e.OriginalSource));
        if (targetDirectory is null)
            return DragDropEffects.None;

        var sources = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        foreach (var source in sources)
        {
            // フォルダを自身／配下へは不可（無限再帰）。
            if (Directory.Exists(source)
                && (PathsEqual(source, targetDirectory) || IsAncestor(source, targetDirectory)))
                return DragDropEffects.None;
            // 同じフォルダーへの移動は何も起きない（「 - コピー」が増えるだけ）ので受けない。
            if (_internalDrag && PathsEqual(Path.GetDirectoryName(source) ?? "", targetDirectory)
                && (e.KeyStates & DragDropKeyStates.ControlKey) == 0)
                return DragDropEffects.None;
        }

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
            return DragDropEffects.Move;
        return _internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    // ===== 小物 =====

    /// <summary>クリック位置の行（行の外＝空き領域なら null）。</summary>
    private static FileEntryViewModel? EntryAt(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null and not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        return (current as ListBoxItem)?.DataContext as FileEntryViewModel;
    }

    /// <summary>選択中の行（一覧の並び順）。</summary>
    private List<FileEntryViewModel> Selection()
        => EntryList.SelectedItems.OfType<FileEntryViewModel>().ToList();

    private FileEntryViewModel? SingleSelection()
    {
        var selection = Selection();
        return selection.Count == 1 ? selection[0] : null;
    }

    private static bool PathsEqual(string a, string b)
        => a.Length > 0 && b.Length > 0
            && string.Equals(
                Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
                StringComparison.OrdinalIgnoreCase);

    private static bool IsAncestor(string folder, string path)
        => sk0ya.Loomo.Core.Files.WorkspacePaths.IsWithin(folder, path) && !PathsEqual(folder, path);

    private static void ShowError(string message) => ToastService.Error(message);
}
