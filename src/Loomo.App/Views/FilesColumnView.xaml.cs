using System.Collections.Specialized;
using System.Globalization;

using sk0ya.Loomo.App.Services;

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
    private FilesColumnViewModel? _boundVm;
    private string _breadcrumbPickerSelectionPath = "";
    private double _placesPaneWidth = 240;

    // Explorer と同じ type-ahead 選択。キー入力が途切れたら次の入力を新しい検索にする
    // （間隔はツリーと同じ 800ms）。
    private string _typeAheadText = string.Empty;
    private readonly DispatcherTimer _typeAheadResetTimer =
        new() { Interval = TimeSpan.FromMilliseconds(800) };

    public FilesColumnView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        _typeAheadResetTimer.Tick += (_, _) =>
        {
            _typeAheadResetTimer.Stop();
            _typeAheadText = string.Empty;
        };
        // 閉じたカラムの裏で ZIP 生成やプロパティ読み取りを走らせ続けない
        // （ZIP は途中の一時ファイルもコマンド側が片付ける）。
        Unloaded += (_, _) =>
        {
            _typeAheadResetTimer.Stop();
            _propertiesLoadCts?.Cancel();
            _zipOperationCts?.Cancel();
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

    // 列幅の変更は見出しの境目に重ねたつまみ（FilesColumnGrip）が受ける。並べ替えは見出しボタンの
    // まま——同じ場所を「押したら並べ替え・端を掴んだら幅」と読み分けさせないのが狙い。
    private static FilesColumnKey? GripColumn(object sender)
        => sender is Thumb { Tag: string tag } && Enum.TryParse<FilesColumnKey>(tag, out var key)
            ? key
            : null;

    // ダブルクリックは幅をその列の中身に合わせる。Thumb のドラッグが始まる前に止めるため
    // Preview で受ける（開始させると2打目でわずかに幅が動く）。
    private void OnColumnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || GripColumn(sender) is not { } key)
            return;
        AutoFitColumn(key);
        e.Handled = true;
    }

    /// <summary>その列の幅を中身に合わせる（境目のダブルクリック）。</summary>
    internal void AutoFitColumn(FilesColumnKey key)
    {
        if (Vm is not { } vm)
            return;
        vm.SetColumnWidth(key, AutoFitWidth(key));
        vm.EndColumnWidthDrag();
    }

    private void OnColumnGripDragStarted(object sender, DragStartedEventArgs e)
        => Vm?.BeginColumnWidthDrag();

    // Thumb の HorizontalChange はつまみ自身から見た移動量で、つまみは幅の変更に追随して動く。
    // そのぶん「いまの幅＋差分」で積むのが正しく、下限で止まっている間も暴走しない。
    private void OnColumnGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (GripColumn(sender) is not { } key || Vm is null)
            return;
        Vm.SetColumnWidth(key, Vm.ColumnWidth(key) + e.HorizontalChange);
    }

    private void OnColumnGripDragCompleted(object sender, DragCompletedEventArgs e)
        => Vm?.EndColumnWidthDrag();

    /// <summary>その列の中身（と見出し）がちょうど収まる幅。ダブルクリックで使う。
    /// 文字の大きさは <c>Fs*</c> をその場で引く——UI の文字サイズは設定で変わるので（§UIフォント）、
    /// ここに数字を焼き込むと大きくしたときだけ測り足りず、合わせたはずの列が見切れる。</summary>
    private double AutoFitWidth(FilesColumnKey key)
    {
        var vm = Vm;
        if (vm is null)
            return 0;

        var headerSize = FontSizeResource("Fs11", 11);
        var nameSize = FontSizeResource("Fs12", 12);
        var cellSize = headerSize;
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        // 状態バッジだけは行の書体と違う（Consolas・SemiBold）。
        var badgeTypeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold,
            FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double TextWidth(string? text, double size, Typeface? face = null)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face ?? typeface, size, Brushes.Black, dpi);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        var setting = vm.ColumnSettings.FirstOrDefault(candidate => candidate.Key == key);
        // 見出しも隠れない幅にする（並べ替え記号は今出ていなくても場所を空けておく——
        // 並べ替え直後に見出しが欠ける方が驚く）。
        var width = TextWidth(setting?.Label + " ▲", headerSize) + HeaderCellExtra;
        // 測るのは「いま出ている行」（絞り込み中は残っている行だけ）。数え切れないほどの
        // フォルダーで測り続けはしない——先頭のぶんで十分に決まる。
        foreach (var entry in vm.EntriesView.Cast<FileEntryViewModel>().Take(AutoFitSampleCount))
        {
            var cell = key switch
            {
                FilesColumnKey.Name => TextWidth(entry.Name, nameSize) + NameCellExtra
                    + TextWidth(entry.GitStatusBadge, headerSize, badgeTypeface),
                FilesColumnKey.Size => TextWidth(entry.SizeText, cellSize) + SizeCellExtra,
                FilesColumnKey.Modified => TextWidth(entry.ModifiedText, cellSize) + CellExtra,
                FilesColumnKey.Type => TextWidth(entry.TypeText, cellSize) + CellExtra,
                _ => 0,
            };
            if (cell > width)
                width = cell;
        }
        // FormattedText の実測と TextBlock の折り返し判定は端数で食い違うことがある。
        // 足りない側へ外すと「合わせたのに…」で終わるので、必ず切り上げてから 1px 足す。
        return Math.Ceiling(width) + 1;
    }

    private double FontSizeResource(string key, double fallback)
        => TryFindResource(key) is double size && size > 0 ? size : fallback;

    // 文字の左右に要る余白。行テンプレートの Margin をそのまま足したもの——XAML 側を変えたらここも合わせる。
    // 名前セル：DockPanel の Margin 6+4 ＋ アイコン 16 ＋ アイコン右 6 ＋ バッジの Margin 6+2（バッジ幅は実測）。
    private const double NameCellExtra = 40;
    // サイズセル：右寄せで Margin 0,0,8,0。左は隣の列との詰まりを避けて 8 見る。
    private const double SizeCellExtra = 16;
    // 更新日時・種類セル：Margin 6,0,0,0 ＋ 右の余裕 8。
    private const double CellExtra = 14;
    // 見出しボタンの Padding 6,0（左右）＋ つまみの線ぶん。
    private const double HeaderCellExtra = 14;
    private const int AutoFitSampleCount = 2000;

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
        // 候補は入力のたびに作り直されるので、その結果に合わせてポップアップを開閉する
        // （OnAddressTextChanged が先に走るため、ここでは新しい候補が見えている）。
        if (e.PropertyName is nameof(FilesColumnViewModel.AddressText)
            or nameof(FilesColumnViewModel.AddressError)
            or nameof(FilesColumnViewModel.IsAddressEditing))
        {
            UpdateAddressSuggestionPopup();
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
            entry => string.Equals(entry.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
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

    private bool _breadcrumbPickerButtonPressed;

    // Popup は外側クリックを先に受けて閉じるため、同じボタンのマウスアップだけが後から届き、
    // そのままだと Click が再発火してポップアップを開き直してしまう。
    private void OnBreadcrumbPickerMouseDown(object sender, MouseButtonEventArgs e)
        => _breadcrumbPickerButtonPressed = true;

    private void OnBreadcrumbPickerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_breadcrumbPickerButtonPressed)
            e.Handled = true;
        _breadcrumbPickerButtonPressed = false;
    }

    /// <summary>パンくずが幅に収まらないときは末尾（現在地）を見せる。左端から切ると、
    /// 狭いカラムで「今どこにいるか」だけが消えることになる。</summary>
    private void OnBreadcrumbScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)
            BreadcrumbScroll.ScrollToRightEnd();
    }

    /// <summary>VS Code のパンくずと同じく、選んだ階層の直下をツリーで開く。
    /// 現在の次階層は選択状態にする。</summary>
    private void OnBreadcrumbPickerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FilesBreadcrumb breadcrumb } target || Vm is null)
            return;

        // キーボード操作など、Popup のマウスキャプチャを経由しない場合も同じトグルにする。
        if (BreadcrumbPickerPopup.IsOpen
            && ReferenceEquals(BreadcrumbPickerPopup.PlacementTarget, target))
        {
            BreadcrumbPickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (!Directory.Exists(breadcrumb.FullPath))
            return;

        var crumbIndex = Vm.Breadcrumbs.IndexOf(breadcrumb);
        _breadcrumbPickerSelectionPath = crumbIndex >= 0 && crumbIndex + 1 < Vm.Breadcrumbs.Count
            ? Vm.Breadcrumbs[crumbIndex + 1].FullPath
            : "";
        BreadcrumbPickerTree.Items.Clear();
        foreach (var path in EnumerateDirectories(breadcrumb.FullPath))
            BreadcrumbPickerTree.Items.Add(CreateBreadcrumbPickerItem(path, _breadcrumbPickerSelectionPath));

        if (BreadcrumbPickerTree.Items.Count == 0)
            return;

        BreadcrumbPickerPopup.PlacementTarget = target;
        BreadcrumbPickerPopup.IsOpen = true;
        e.Handled = true;
    }

    private TreeViewItem CreateBreadcrumbPickerItem(string path, string currentPath)
    {
        var item = new TreeViewItem
        {
            Header = CreateBreadcrumbPickerHeader(path),
            Tag = path,
            IsSelected = string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase),
        };
        item.Expanded += OnBreadcrumbPickerItemExpanded;
        if (HasDirectories(path))
            item.Items.Add(new TreeViewItem { Tag = null, IsHitTestVisible = false });
        return item;
    }

    private static StackPanel CreateBreadcrumbPickerHeader(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image
        {
            Source = FileIcons.FolderImage(open: false),
            Width = 14,
            Height = 14,
            Margin = new Thickness(1, 0, 5, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(name) ? path : name,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    private void OnBreadcrumbPickerItemExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string path
            || item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null })
            return;

        item.Items.Clear();
        foreach (var child in EnumerateDirectories(path))
            item.Items.Add(CreateBreadcrumbPickerItem(child, _breadcrumbPickerSelectionPath));
        e.Handled = true;
    }

    private void OnBreadcrumbPickerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || FindVisualParent<ToggleButton>(source) is not null
            || FindVisualParent<TreeViewItem>(source) is not { Tag: string path })
            return;

        Vm?.Navigate(path);
        BreadcrumbPickerPopup.IsOpen = false;
        e.Handled = true;
    }

    private static IEnumerable<string> EnumerateDirectories(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool HasDirectories(string path) => EnumerateDirectories(path).Any();

    private static T? FindVisualParent<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }
        return null;
    }

    /// <summary>場所は常設の縦パネルであってポップアップではないので、項目を開いても畳まない。
    /// 閉じるのはツールバーの「場所」ボタンを押したときだけにする（続けて別の場所へ飛べる）。</summary>
    private void OnPlaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FilesPlace place })
            Vm?.OpenPlace(place);
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

        // Alt+←／→ は戻る／進む、Alt+Enter はプロパティ（Alt 付きは SystemKey に入る）。
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
            else if (systemKey is Key.Enter or Key.Return)
            {
                ShowProperties();
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
                // ファイル操作の元に戻す／やり直す（履歴はエクスプローラーのツリーと共有）。
                case Key.Z:
                    if ((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0)
                        RedoFileOperation();
                    else
                        UndoFileOperation();
                    e.Handled = true;
                    return;
                case Key.Y:
                    RedoFileOperation();
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
            // TUI ファイラーと同じ j/k 移動。ツリーと同じ語彙にそろえる（そちらは §Vim 操作として
            // 先に入っていた）。1文字目が j/k のファイルへは type-ahead ではなく「/」の絞り込みで届く。
            case Key.J:
                MoveSelection(delta: 1);
                e.Handled = true;
                break;
            case Key.K:
                MoveSelection(delta: -1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>表示順（絞り込み・並べ替え・グループ化の後）の隣へ選択を移す。端では止まる。</summary>
    private void MoveSelection(int delta)
    {
        var items = EntryList.Items.OfType<FileEntryViewModel>().ToList();
        var currentIndex = EntryList.SelectedItem is FileEntryViewModel current
            ? items.IndexOf(current)
            : -1;
        var index = FolderTreeKeyboardNavigation.FindAdjacentIndex(items.Count, currentIndex, delta);
        if (index < 0)
            return;
        EntryList.SelectedItems.Clear();
        EntryList.SelectedItem = items[index];
        EntryList.ScrollIntoView(items[index]);
    }

    // 一覧への直接の文字入力は Explorer と同じ type-ahead 選択にする。j/k は上の KeyDown で
    // 移動として処理済みなので、ここには通常の文字入力だけが届く。
    private void OnListPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        var items = EntryList.Items.OfType<FileEntryViewModel>().ToList();
        if (items.Count == 0)
            return;

        _typeAheadResetTimer.Stop();
        _typeAheadText += e.Text;
        var names = items.Select(entry => entry.Name).ToList();
        var currentIndex = EntryList.SelectedItem is FileEntryViewModel current
            ? items.IndexOf(current)
            : -1;
        var matchIndex = FolderTreeKeyboardNavigation.FindTypeAheadMatch(names, _typeAheadText, currentIndex);

        // 入力が続いて一致しなくなった場合は、最後の文字を新しい検索の先頭として試す。
        if (matchIndex < 0 && _typeAheadText.Length > e.Text.Length)
        {
            _typeAheadText = e.Text;
            matchIndex = FolderTreeKeyboardNavigation.FindTypeAheadMatch(names, _typeAheadText, currentIndex);
        }

        if (matchIndex >= 0)
        {
            EntryList.SelectedItems.Clear();
            EntryList.SelectedItem = items[matchIndex];
            EntryList.ScrollIntoView(items[matchIndex]);
        }

        _typeAheadResetTimer.Start();
        e.Handled = true;
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
                // Windows Explorer 側のクイックアクセス（Loomo のルートピンとは別物）。
                "QuickAccessPinnable" => Vm.CanPinToQuickAccess(selection),
                "QuickAccessUnpinnable" => Vm.CanUnpinFromQuickAccess(selection),
                "GitMenu" => Vm.CanGitFor(single),
                "GitBlame" => single is { IsDirectory: false } && Vm.CanGitFor(single),
                "GitIgnore" => Vm.CanAddToGitignoreFor(single),
                "AiMenu" => selection.Count > 0 && Vm.CanRunFileAi,
                // Undo/Redo は選択ではなく履歴で決まる（下の UpdateHistoryMenuItems が出し分ける）。
                "UndoItem" or "RedoItem" => item.Visibility == Visibility.Visible,
                _ => true,
            };
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateHistoryMenuItems(menu);
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

    private void OnFileAiClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
            return;
        var action = ((sender as MenuItem)?.Tag as string) switch
        {
            "FileAiSummarize" => FileAiAction.Summarize,
            "FileAiReview" => FileAiAction.Review,
            "FileAiGenerateTests" => FileAiAction.GenerateTests,
            "FileAiFindRelated" => FileAiAction.FindRelated,
            _ => (FileAiAction?)null,
        };
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
    {
        if (SingleSelection() is { } entry)
        {
            try { Vm?.AddToGitignore(entry); }
            catch (InvalidOperationException ex) { ShowError(ex.Message); }
        }
    }

    private void OnNewFileClick(object sender, RoutedEventArgs e) => CreateEntry(isDirectory: false);

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => CreateEntry(isDirectory: true);

    private void CreateEntry(bool isDirectory)
    {
        if (Vm is not { TargetDirectory: not null })
            return;
        var title = isDirectory ? "新規フォルダー" : "新規ファイル";
        var name = isDirectory
            ? InputDialog.Prompt(OwnerWindow, title, $"{title}名を入力:")
            : NewFileDialog.Prompt(OwnerWindow);
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

        // 選択ぶんは 1 回の Undo でまとめて戻す。
        using (Vm.BeginFileOperationBatch())
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
        using (Vm.BeginFileOperationBatch())
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
        FileConflictDecision? applyToAll = null;
        var cancelled = false;

        FileConflictDecision ResolveConflict(FileConflictContext context)
        {
            if (applyToAll is { } remembered)
                return remembered;
            var decision = FileConflictDialog.Show(OwnerWindow, context);
            if (decision.ApplyToAll && decision.Action is (FileConflictAction.Overwrite or FileConflictAction.Skip))
                applyToAll = decision with { ApplyToAll = false };
            return decision;
        }

        try
        {
            using (Vm.BeginFileOperationBatch())
                foreach (var source in FileClipboard.GetFiles())
                {
                    var result = Vm.PasteEntry(target, source, move, ResolveConflict);
                    if (result.Cancelled)
                    {
                        cancelled = true;
                        break;
                    }
                }
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        // 切り取り→貼り付け（移動）はエクスプローラー同様、成功後にクリップボードを空にする。
        if (move && !cancelled)
            FileClipboard.Clear();
    }

    // ===== 元に戻す／やり直す（ファイル操作の Undo/Redo・ツリーと共有の履歴） =====

    private void OnUndoFileOperationClick(object sender, RoutedEventArgs e) => UndoFileOperation();

    private void OnRedoFileOperationClick(object sender, RoutedEventArgs e) => RedoFileOperation();

    private void UndoFileOperation() => RunHistoryStep(undo: true);

    private void RedoFileOperation() => RunHistoryStep(undo: false);

    private async void RunHistoryStep(bool undo)
    {
        if (Vm is null || (undo ? !Vm.History.CanUndo : !Vm.History.CanRedo))
            return;

        try
        {
            var result = undo
                ? Vm.UndoFileOperation()
                : await Vm.RedoFileOperationAsync();
            ToastService.Info($"{(undo ? "元に戻しました" : "やり直しました")}: {result.Description}");
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    // 「元に戻す」「やり直す」の見出しを次の一手に合わせ、無いときは項目ごと隠す。
    private void UpdateHistoryMenuItems(ContextMenu menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            switch (item.Tag as string)
            {
                case "UndoItem": ApplyHistoryHeader(item, "元に戻す", Vm?.History.UndoDescription); break;
                case "RedoItem": ApplyHistoryHeader(item, "やり直す", Vm?.History.RedoDescription); break;
            }
    }

    private static void ApplyHistoryHeader(MenuItem item, string verb, string? description)
    {
        item.Visibility = description is null ? Visibility.Collapsed : Visibility.Visible;
        item.Header = description is null ? verb : $"{verb}（{description}）";
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
        FileDragDrop.SetPaths(data, paths);

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
        var sources = FileDragDrop.TryGetPaths(e.Data);
        if (effect == DragDropEffects.None || targetDirectory is null || Vm is null
            || sources.Count == 0)
            return;

        var move = (effect & DragDropEffects.Move) != 0;
        FileConflictDecision? applyToAll = null;
        FileConflictDecision ResolveConflict(FileConflictContext context)
        {
            if (applyToAll is { } remembered)
                return remembered;
            var decision = FileConflictDialog.Show(OwnerWindow, context);
            if (decision.ApplyToAll && decision.Action is (FileConflictAction.Overwrite or FileConflictAction.Skip))
                applyToAll = decision with { ApplyToAll = false };
            return decision;
        }

        try
        {
            using (Vm.BeginFileOperationBatch())
                foreach (var source in sources)
                    if (!string.IsNullOrEmpty(source))
                    {
                        var result = Vm.PasteEntry(targetDirectory, source, move, ResolveConflict);
                        if (result.Cancelled)
                            break;
                    }
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

        var sources = FileDragDrop.TryGetPaths(e.Data);
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
