namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ActivityBar 2本（上段／中段）と、それぞれが持つサイドバー区画。
/// パネルのビューは XAML で1つずつ宣言しておき、ここが「どちらのホストの子にするか」と
/// 「どれを見せるか」を決める——段を移しても状態（スクロール位置・展開）を持ったまま動く。
/// 項目はドラッグ＆ドロップで段をまたげる（並びは <see cref="ViewModels.ActivityBarViewModel"/> が保存する）。</summary>
public partial class ShellWindow {
    /// <summary>中段区画の既定高さ（px）。スプリッターのダブルクリックでここへ戻す。</summary>
    private const double DefaultSecondarySectionHeight = 200;
    /// <summary>畳む直前の中段の高さ。開き直したときにここへ戻す。</summary>
    private double _savedSecondarySectionHeight = DefaultSecondarySectionHeight;
    /// <summary>直前の配置で上下どちらの区画も出ていたか。中段の実測値を覚えてよいのはこのときだけ
    /// ——片方しか出ていない間の中段は行いっぱい（*）なので、その高さを覚えると開き直したときに
    /// 上段を最小まで潰してしまう。</summary>
    private bool _sidebarSectionsBothShown = true;
    /// <summary>ActivityBar の項目ドラッグで運ぶ中身の形式名。</summary>
    private const string ActivityItemFormat = "Loomo.ActivityBarItem";
    private Point _activityDragOrigin;
    private ActivityBarItemViewModel? _activityDragItem;
    /// <summary>直前のシングル押下の時点のサイドバー状態。ダブルクリックの判断に使う。</summary>
    private ShellViewModel.SidebarSections _sidebarsBeforeClick;

    /// <summary>パネル種別 → XAML で宣言したビュー。載せ替えの対象はこの7つ。</summary>
    private IReadOnlyDictionary<SidebarPanel, FrameworkElement> SidebarPanelViews => _sidebarPanelViews
        ??= new Dictionary<SidebarPanel, FrameworkElement> {
            [SidebarPanel.Explorer] = SidebarFolderTree,
            [SidebarPanel.Git] = SidebarGitPanel,
            [SidebarPanel.Pegboard] = SidebarPegboard,
            [SidebarPanel.Solution] = SidebarSolution,
            [SidebarPanel.Tabs] = SidebarTabs,
            [SidebarPanel.Todo] = SidebarTodoTree,
            [SidebarPanel.WorkItems] = SidebarWorkItems,
            [SidebarPanel.Running] = SidebarRunning,
        };
    private IReadOnlyDictionary<SidebarPanel, FrameworkElement>? _sidebarPanelViews;

    private void InitializeActivityBar() {
        var height = _settings.ActivityBar.SecondaryHeight;
        _savedSecondarySectionHeight = double.IsFinite(height) && height >= 26
            ? height : DefaultSecondarySectionHeight;
        _sidebarSectionsBothShown = false;
        Closing += (_, _) => {
            if (_sidebarSectionsBothShown && SecondarySidebarRow.ActualHeight >= 26)
                _savedSecondarySectionHeight = SecondarySidebarRow.ActualHeight;
            _settings.ActivityBar.SecondaryHeight = _savedSecondarySectionHeight;
            _vm.ActivityBar.Persist();
        };
        SidebarSectionSplitter.Cursor = Cursors.SizeNS;
        SidebarSectionSplitter.MouseEnter += (_, _) => SidebarSectionSplitter.Background = (Brush)FindResource("Accent");
        SidebarSectionSplitter.MouseLeave += (_, _) => SidebarSectionSplitter.Background = (Brush)FindResource("Border");
        SidebarSectionSplitter.MouseDoubleClick += (_, _) => {
            _savedSecondarySectionHeight = DefaultSecondarySectionHeight;
            SecondarySidebarRow.Height = new GridLength(DefaultSecondarySectionHeight);
        };
        _vm.ActivityBar.ItemMoved += (_, _) => ApplySidebarLayout();
        SidebarContainer.PreviewMouseLeftButtonDown += OnSidebarHeaderMouseDown;
        SidebarContainer.PreviewMouseMove += OnSidebarHeaderMouseMove;
        SidebarContainer.PreviewMouseLeftButtonUp += (_, _) => _sidebarHeaderDragItem = null;
        _vm.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(ShellViewModel.ActivePanel) or nameof(ShellViewModel.SecondaryPanel)
                or nameof(ShellViewModel.IsSidebarVisible) or nameof(ShellViewModel.IsSecondarySidebarVisible))
                ApplySidebarLayout();
        };
        ApplySidebarLayout();
    }

    /// <summary>各パネルを住んでいる段のホストへ載せ、その段で選ばれている1面だけを見せる。
    /// 段の中身が無い／畳まれていれば、その区画は行ごと 0 にする。</summary>
    private void ApplySidebarLayout() {
        foreach (var (panel, view) in SidebarPanelViews) {
            var slot = _vm.ActivityBar.SlotOf(panel);
            var host = slot == ActivityBarSlot.Secondary ? SecondarySidebarHost : PrimarySidebarHost;
            if (!ReferenceEquals(view.Parent, host)) {
                (view.Parent as Panel)?.Children.Remove(view);
                host.Children.Add(view);
            }
            var showing = slot == ActivityBarSlot.Secondary
                ? _vm.IsSecondarySidebarVisible && _vm.SecondaryPanel == panel
                : _vm.IsSidebarVisible && _vm.ActivePanel == panel;
            view.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplySidebarSectionHeights();
    }

    private void ApplySidebarSectionHeights() {
        // 配置を変える前に、いまの中段の高さを覚える——スプリッターのドラッグ後の行は px とは限らない
        // （* へ変わることがある）ので Height ではなく ActualHeight を見る。覚えてよいのは
        // 「上下どちらも出ていた」間の値だけ。片方だけのときの中段は行いっぱいなので数えない。
        if (_sidebarSectionsBothShown && SecondarySidebarRow.ActualHeight >= 26)
            _savedSecondarySectionHeight = SecondarySidebarRow.ActualHeight;

        var primaryShown = _vm.IsSidebarVisible
            && _vm.ActivityBar.Holds(ActivityBarSlot.Primary, _vm.ActivePanel);
        var secondaryShown = _vm.IsSecondarySidebarVisible
            && _vm.ActivityBar.Holds(ActivityBarSlot.Secondary, _vm.SecondaryPanel);
        _sidebarSectionsBothShown = primaryShown && secondaryShown;

        PrimarySidebarHost.Visibility = primaryShown ? Visibility.Visible : Visibility.Collapsed;
        SecondarySidebarHost.Visibility = secondaryShown ? Visibility.Visible : Visibility.Collapsed;
        PrimarySidebarRow.MinHeight = primaryShown ? 80 : 0;
        PrimarySidebarRow.Height = primaryShown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SecondarySidebarRow.MinHeight = secondaryShown ? 26 : 0;
        SecondarySidebarRow.Height = secondaryShown
            ? primaryShown ? new GridLength(_savedSecondarySectionHeight) : new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        // 境目を掴めるのは、上下どちらにも中身があるときだけ。
        SidebarSectionSplitter.Visibility = primaryShown && secondaryShown
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 項目のドラッグ＆ドロップ（段の移動・並べ替え） =====

    private void OnActivityItemMouseDown(object sender, MouseButtonEventArgs e) {
        // すばやいダブルクリック＝サイドバーを全部畳む／全部閉じていたなら畳む前へ戻す。
        // 2回目の押下を Button に渡さないことで2回目の Click（＝開閉のトグル）を起こさせない。
        // 判断は1回目のクリックより前の状態で行う（1回目で開閉が一度切り替わっているため）。
        if (e.ClickCount == 2) {
            _activityDragItem = null;
            _vm.ToggleAllSidebars(_sidebarsBeforeClick);
            e.Handled = true;
            return;
        }
        _sidebarsBeforeClick = _vm.CaptureSidebars();
        _activityDragOrigin = e.GetPosition(this);
        _activityDragItem = (sender as FrameworkElement)?.DataContext as ActivityBarItemViewModel;
    }

    /// <summary>押しただけ（ドラッグにならなかった）なら掴んだ印を捨てる。持ち越すと、次にボタンを
    /// 押したまま別のアイコンの上を通っただけで、そこに無い項目を掴んで動かしてしまう。</summary>
    private void OnActivityItemMouseUp(object sender, MouseButtonEventArgs e) => _activityDragItem = null;

    private void OnActivityItemMouseMove(object sender, MouseEventArgs e) {
        if (_activityDragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        // 掴んだのは「いまマウスの下にある項目」でなければならない。
        if ((sender as FrameworkElement)?.DataContext is not ActivityBarItemViewModel hovered
            || !ReferenceEquals(hovered, _activityDragItem))
            return;
        var delta = e.GetPosition(this) - _activityDragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _activityDragItem;
        _activityDragItem = null;   // DoDragDrop は入れ子のメッセージループなので、先に手を放す
        var data = new DataObject(ActivityItemFormat, item);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        HideActivityDropMarks();
    }

    private void OnActivityGroupDragOver(object sender, DragEventArgs e) {
        if (ResolveActivityGroup(sender) is not { } group || DraggedActivityItem(e) is null) {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var markY = ResolveActivityDropIndex(group.List, e.GetPosition((IInputElement)sender).Y).MarkY;
        group.Mark.Margin = new Thickness(6, markY, 6, 0);
        group.Mark.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnActivityGroupDragLeave(object sender, DragEventArgs e) => HideActivityDropMarks();

    private void OnActivityGroupDrop(object sender, DragEventArgs e) {
        HideActivityDropMarks();
        if (ResolveActivityGroup(sender) is not { } group || DraggedActivityItem(e) is not { } item)
            return;
        var (index, _) = ResolveActivityDropIndex(group.List, e.GetPosition((IInputElement)sender).Y);
        _vm.ActivityBar.Move(item, group.Slot, index);
        e.Handled = true;
    }

    private static ActivityBarItemViewModel? DraggedActivityItem(DragEventArgs e)
        => e.Data.GetDataPresent(ActivityItemFormat)
            ? e.Data.GetData(ActivityItemFormat) as ActivityBarItemViewModel : null;

    /// <summary>落とし先の段（Tag）と、その段の一覧・挿入線。</summary>
    private (ItemsControl List, ActivityBarSlot Slot, Border Mark)? ResolveActivityGroup(object sender)
        => sender switch {
            FrameworkElement { Tag: "Primary" } => (PrimaryActivityItems, ActivityBarSlot.Primary, PrimaryActivityDropMark),
            FrameworkElement { Tag: "Secondary" } => (SecondaryActivityItems, ActivityBarSlot.Secondary, SecondaryActivityDropMark),
            _ => null,
        };

    /// <summary>縦位置から挿入先の添字と、挿入線を引く y を決める。</summary>
    private (int Index, double MarkY) ResolveActivityDropIndex(ItemsControl list, double y) {
        var index = 0;
        var markY = list.TranslatePoint(new Point(0, 0), (UIElement)list.Parent).Y;
        for (var i = 0; i < list.Items.Count; i++) {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement { IsVisible: true } container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), (UIElement)list.Parent).Y;
            if (y < top + container.ActualHeight / 2)
                break;
            index = i + 1;
            markY = top + container.ActualHeight;
        }
        return (index, markY);
    }

    private void HideActivityDropMarks() {
        PrimaryActivityDropMark.Visibility = Visibility.Collapsed;
        SecondaryActivityDropMark.Visibility = Visibility.Collapsed;
    }

    // ===== パネルの見出しを掴んで段を移す =====
    // ActivityBar のアイコンを運ぶのと同じ中身（ActivityItemFormat）を運ぶので、アイコンの列へ落としても、
    // サイドバーの上半分（上段）／下半分（中段）へ落としてもよい。

    private Point _sidebarHeaderDragOrigin;
    private ActivityBarItemViewModel? _sidebarHeaderDragItem;

    private void OnSidebarHeaderMouseDown(object sender, MouseButtonEventArgs e) {
        _sidebarHeaderDragItem = null;
        if (e.ClickCount != 1 || SidebarPanelOfHeader(e.OriginalSource as DependencyObject) is not { } panel)
            return;
        _sidebarHeaderDragItem = _vm.ActivityBar.ItemFor(panel);
        _sidebarHeaderDragOrigin = e.GetPosition(this);
    }

    private void OnSidebarHeaderMouseMove(object sender, MouseEventArgs e) {
        if (_sidebarHeaderDragItem is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed) {
            _sidebarHeaderDragItem = null;
            return;
        }
        var delta = e.GetPosition(this) - _sidebarHeaderDragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _sidebarHeaderDragItem;
        _sidebarHeaderDragItem = null;   // DoDragDrop は入れ子のメッセージループなので、先に手を放す
        SidebarDropPreview.Background = TranslucentAccent(0.25);
        SidebarDropOverlay.Visibility = Visibility.Visible;
        try {
            DragDrop.DoDragDrop(SidebarContainer, new DataObject(ActivityItemFormat, item), DragDropEffects.Move);
        } finally {
            SidebarDropOverlay.Visibility = Visibility.Collapsed;
            SidebarDropPreview.Visibility = Visibility.Collapsed;
            HideActivityDropMarks();
        }
    }

    /// <summary>押した場所がサイドバーのパネルの見出し帯（<c>SidebarHeaderBand</c>）の空きなら、そのパネル。
    /// 見出しに置いたボタン・タブ（Git の「変更／比較」）・入力欄は押す場所であって取っ手ではない。</summary>
    private SidebarPanel? SidebarPanelOfHeader(DependencyObject? source) {
        var headerStyle = TryFindResource("SidebarHeaderBand");
        var onHeader = false;
        for (var current = source; current is not null && !ReferenceEquals(current, SidebarContainer);
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current)) {
            if (!onHeader) {
                if (current is ButtonBase or ComboBox or TabItem or TextBoxBase)
                    return null;
                if (current is Border border && headerStyle is not null && ReferenceEquals(border.Style, headerStyle))
                    onHeader = true;
                continue;
            }
            foreach (var (panel, view) in SidebarPanelViews)
                if (ReferenceEquals(view, current))
                    return panel;
        }
        return null;
    }

    /// <summary>サイドバーの受け皿：上半分＝上段・下半分＝中段。両段とも出ていれば、その段の実際の区画を塗る。</summary>
    private (ActivityBarSlot Slot, Rect Preview)? ResolveSidebarDropZone(DragEventArgs e) {
        var width = SidebarDropOverlay.ActualWidth;
        var height = SidebarDropOverlay.ActualHeight;
        if (width <= 0 || height <= 0)
            return null;
        var slot = e.GetPosition(SidebarDropOverlay).Y < height / 2 ? ActivityBarSlot.Primary : ActivityBarSlot.Secondary;
        var preview = slot == ActivityBarSlot.Primary
            ? new Rect(0, 0, width, height / 2)
            : new Rect(0, height / 2, width, height / 2);
        if (_sidebarSectionsBothShown) {
            var host = slot == ActivityBarSlot.Primary ? PrimarySidebarHost : SecondarySidebarHost;
            var origin = host.TranslatePoint(new Point(0, 0), SidebarDropOverlay);
            preview = new Rect(origin, new Size(host.ActualWidth, host.ActualHeight));
        }
        return (slot, preview);
    }

    private void OnSidebarOverlayDragOver(object sender, DragEventArgs e) {
        e.Handled = true;
        // 今いる段へ落としても何も起きないので、受けない（並べ替えはアイコンの列で）。
        if (DraggedActivityItem(e) is not { } item || ResolveSidebarDropZone(e) is not { } zone
            || _vm.ActivityBar.SlotOf(item.Panel) == zone.Slot) {
            e.Effects = DragDropEffects.None;
            SidebarDropPreview.Visibility = Visibility.Collapsed;
            return;
        }
        e.Effects = DragDropEffects.Move;
        Canvas.SetLeft(SidebarDropPreview, zone.Preview.X);
        Canvas.SetTop(SidebarDropPreview, zone.Preview.Y);
        SidebarDropPreview.Width = zone.Preview.Width;
        SidebarDropPreview.Height = zone.Preview.Height;
        SidebarDropPreview.Visibility = Visibility.Visible;
    }

    private void OnSidebarOverlayDragLeave(object sender, DragEventArgs e)
        => SidebarDropPreview.Visibility = Visibility.Collapsed;

    private void OnSidebarOverlayDrop(object sender, DragEventArgs e) {
        e.Handled = true;
        SidebarDropPreview.Visibility = Visibility.Collapsed;
        if (DraggedActivityItem(e) is not { } item || ResolveSidebarDropZone(e) is not { } zone
            || _vm.ActivityBar.SlotOf(item.Panel) == zone.Slot)
            return;
        _vm.ActivityBar.Move(item, zone.Slot, _vm.ActivityBar.ItemsIn(zone.Slot).Count);   // 移した先の段の末尾へ
    }
}
