using System.Windows.Automation;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 「Viewer」ペインの中身（§44.2）。タブ 1 つに Viewer 1 つ——置き場の Viewer か、<b>パスを渡して開いたもの</b>
/// （HTML の頁・EditorSupport の表示・プログラムの窓・Claude Code が作った Viewer）。上にタブの並びと「＋」、下に前に出ている
/// タブの中身。中身を出せないとき（まだ許していない・作っている途中・起動していない…）は、同じ場所にカードを出す。
/// <para>読み込むのは<b>前に出ているタブだけ</b>——復元したタブは選ばれるまで中身を作らない。</para>
/// XAML を持たない（部品が少なく、色と字の大きさは部屋のトークンをそのまま引く）。
/// </summary>
internal sealed class ViewerPaneView : UserControl
{
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _contentHost = new();
    private readonly Border _card = new() { Visibility = Visibility.Collapsed };
    private readonly List<IViewerTab> _tabs = [];
    private readonly DispatcherTimer _hintTimer;
    private IViewerTab? _active;
    private ViewerTabFactory? _factory;
    private bool _openCardShown;
    private TextBox? _pathBox;
    private TextBlock? _hint;

    public ViewerPaneView()
    {
        Focusable = true;
        AllowDrop = true;
        SetResourceReference(BackgroundProperty, "Panel");
        AutomationProperties.SetAutomationId(this, "ViewerPane");

        var add = new Button { Content = "＋", ToolTip = "パスを指定して開く・置いてある Viewer・Viewer のフォルダー", Width = 28, Padding = new Thickness(0) };
        add.SetResourceReference(StyleProperty, "PaneHeaderGhostButton");
        AutomationProperties.SetAutomationId(add, "ViewerAddButton");
        AutomationProperties.SetName(add, "Viewer を開く");
        add.Click += (_, _) => OpenMenu(add);
        DockPanel.SetDock(add, Dock.Right);

        var strip = new DockPanel { LastChildFill = true, Height = 26 };
        strip.Children.Add(add);
        strip.Children.Add(new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var stripBorder = new Border { Child = strip, BorderThickness = new Thickness(0, 0, 0, 1) };
        stripBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(stripBorder, Dock.Top);

        var body = new Grid();
        body.Children.Add(_contentHost);
        body.Children.Add(_card);
        _card.SetResourceReference(Border.BackgroundProperty, "Panel");

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(stripBorder);
        root.Children.Add(body);
        Content = root;

        _hintTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _ = UpdateHintAsync();
        };
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>「Viewer を生やす」を押した（頼みごとの下書きを渡すのは部屋の仕事）。</summary>
    public event EventHandler? GrowRequested;

    /// <summary>タブの並び・題・前に出ているタブが変わった（TABS へ写す）。</summary>
    public event EventHandler? TabsChanged;

    /// <summary>タブの右クリック「別ウィンドウで開く」。</summary>
    public event EventHandler<IViewerTab>? DetachRequested;

    public IReadOnlyList<IViewerTab> Tabs => _tabs;
    public IViewerTab? ActiveTab => _active;

    public void Configure(ViewerTabFactory factory)
    {
        _factory = factory;
        // 前に開いていたタブを戻す（数えない・前に出ていた 1 つだけ読み込む・プログラムは起動しない）。
        var (open, active) = factory.State.OpenViewers;
        foreach (var key in open)
        {
            if (factory.ForKey(key, _contentHost) is { } tab)
            {
                Attach(tab);
                _ = tab.PeekAsync();
            }
        }
        Activate(_tabs.FirstOrDefault(tab => tab.Key == active) ?? _tabs.FirstOrDefault(), persist: false);
    }

    /// <summary>置き場の Viewer をタブに出す（すでに出ていればそれを前へ）。定義が読めなくてもタブは出す——理由がカードに出る。</summary>
    public async Task<ViewerTabHost?> ShowViewerAsync(string id)
    {
        if (_factory is null || !ViewerManifest.IsValidId(id))
            return null;
        var viewer = _tabs.OfType<ViewerTabHost>().FirstOrDefault(existing => existing.Id == id && existing.TargetPath is null);
        if (viewer is null)
        {
            viewer = _factory.ForViewer(id, _contentHost);
            Attach(viewer);
            _factory.State.RecordOpened(id, DateTime.Today);
        }
        // 先に定義を読み直してから前へ出す（前へ出したときの読み込みと二重に走らせない）。
        await viewer.RefreshAsync();
        Activate(viewer, persist: true);
        return viewer;
    }

    /// <summary>パスを開く（すでに開いていればそれを前へ）。見せ方は Loomo が決める。開けなければ null と通知。</summary>
    public IViewerTab? OpenPath(string input)
    {
        if (_factory is null)
            return null;
        if (ViewerTargets.WebUrl(input) is { } url)
        {
            var web = _tabs.OfType<ViewerWebTab>().FirstOrDefault(tab => tab.Url == url) as IViewerTab ?? _factory.ForUrl(url, _contentHost);
            if (!_tabs.Contains(web))
                Attach(web);
            HideOpenCard();
            Activate(web, persist: true);
            return web;
        }
        if (ViewerTargets.Normalize(input) is not { } path)
        {
            ToastService.Error("パスとして読めません：" + input);
            return null;
        }
        if (_tabs.FirstOrDefault(tab => string.Equals(tab.TargetPath, path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            HideOpenCard();
            Activate(existing, persist: true);
            return existing;
        }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ToastService.Error("見つかりません：" + path);
            return null;
        }
        var tab = _factory.ForPath(path, _contentHost, fresh: true);
        Attach(tab);
        HideOpenCard();
        Activate(tab, persist: true);
        return tab;
    }

    /// <summary>「パスを指定して開く」のカードを出す（TABS の「＋」・パレット・ペインの「＋」から）。</summary>
    public void ShowOpenCard(string? initial = null)
    {
        _openCardShown = true;
        RenderCard();
        if (_pathBox is not null)
        {
            if (initial is not null)
                _pathBox.Text = initial;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                _pathBox?.Focus();
                _pathBox?.SelectAll();
            });
        }
    }

    public void ActivateTab(Guid tabId)
    {
        if (_tabs.FirstOrDefault(tab => tab.TabId == tabId) is { } tab)
            Activate(tab, persist: true);
    }

    public void CloseTab(Guid tabId)
    {
        if (_tabs.FirstOrDefault(tab => tab.TabId == tabId) is { } tab)
            Close(tab);
    }

    /// <summary>タブを並びから外して渡す（捨てない）。切り離した窓へ移すとき。</summary>
    public IViewerTab? Take(Guid tabId)
    {
        if (_tabs.FirstOrDefault(tab => tab.TabId == tabId) is not { } tab)
            return null;
        Remove(tab, dispose: false);
        return tab;
    }

    /// <summary>よそ（切り離した窓）から戻ってきたタブを並びに入れて前へ。</summary>
    public void Adopt(IViewerTab tab)
    {
        tab.Rehost(_contentHost);
        Attach(tab);
        Activate(tab, persist: true);
    }

    /// <summary>部屋の出来事を、出ている Viewer へ流す。</summary>
    public void Emit(string name, JsonNode? data, string requiredApi)
    {
        foreach (var viewer in _tabs.OfType<ViewerTabHost>())
            viewer.Emit(name, data?.DeepClone(), requiredApi);
    }

    public void FocusActive()
    {
        if (_openCardShown && _pathBox is not null)
            _pathBox.Focus();
        else if (_active is { Status: ViewerTabStatus.Running } tab)
            tab.Focus();
        else
            Focus();
    }

    private void Attach(IViewerTab tab)
    {
        tab.Changed += OnTabChanged;
        tab.CloseRequested += OnTabCloseRequested;
        if (tab is ViewerGeneratingTab generating)
            generating.Completed += OnGenerationCompleted;
        if (tab is ViewerTabBase based)
            based.GenerateRequested += OnGenerateRequested;
        _tabs.Add(tab);
    }

    private void Detach(IViewerTab tab)
    {
        tab.Changed -= OnTabChanged;
        tab.CloseRequested -= OnTabCloseRequested;
        if (tab is ViewerGeneratingTab generating)
            generating.Completed -= OnGenerationCompleted;
        if (tab is ViewerTabBase based)
            based.GenerateRequested -= OnGenerateRequested;
    }

    /// <summary>「Claude Code に見せ方を作ってもらう」：同じ場所を、作っている途中のタブに差し替える。</summary>
    private void OnGenerateRequested(object? sender, EventArgs e)
    {
        if (sender is IViewerTab { TargetPath: { } path } tab && _factory is not null)
            Replace(tab, _factory.Generate(path, _contentHost));
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        Persist();   // URL のタブは、見ている頁が変わると鍵も変わる
        RenderTabs();
        if (ReferenceEquals(sender, _active))
            RenderCard();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is IViewerTab tab)
            Close(tab);
    }

    /// <summary>作り終えた（またはやめた作り直し）：同じ場所を、その Viewer でそのファイルを開いたタブに差し替える。</summary>
    private void OnGenerationCompleted(object? sender, EventArgs e)
    {
        if (sender is not ViewerGeneratingTab generating || _factory is null || generating.TargetPath is not { } path)
            return;
        var replacement = _factory.ForViewer(generating.ViewerId, _contentHost, path);
        Replace(generating, replacement);
        _ = replacement.RefreshAsync();
    }

    private void Replace(IViewerTab old, IViewerTab replacement)
    {
        var index = _tabs.IndexOf(old);
        if (index < 0)
            return;
        var wasActive = ReferenceEquals(old, _active);
        Detach(old);
        old.Dispose();
        _tabs.RemoveAt(index);
        Attach(replacement);
        _tabs.Remove(replacement);
        _tabs.Insert(index, replacement);
        if (wasActive)
            Activate(replacement, persist: true);
        else
        {
            replacement.SetVisible(false);
            RenderTabs();
            Persist();
        }
    }

    private void Activate(IViewerTab? tab, bool persist)
    {
        _active = tab;
        foreach (var other in _tabs)
            other.SetVisible(ReferenceEquals(other, tab));
        if (tab is { Status: ViewerTabStatus.Pending })
            _ = tab.RefreshAsync();
        RenderTabs();
        RenderCard();
        if (persist)
            Persist();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Close(IViewerTab tab) => Remove(tab, dispose: true);

    private void Remove(IViewerTab tab, bool dispose)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        _tabs.RemoveAt(index);
        Detach(tab);
        if (dispose)
            tab.Dispose();
        else
            tab.SetVisible(false);
        if (ReferenceEquals(tab, _active))
            Activate(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)], persist: false);
        else
            RenderTabs();
        Persist();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Persist() => _factory?.State.SetOpenViewers(_tabs.Select(tab => tab.Key), _active?.Key);

    // ── 見え方 ───────────────────────────────────────────────

    private void RenderTabs()
    {
        _strip.Children.Clear();
        foreach (var tab in _tabs)
        {
            var isActive = ReferenceEquals(tab, _active);
            var title = new TextBlock
            {
                Text = tab.Status switch
                {
                    ViewerTabStatus.Broken => "⚠ " + tab.Title,
                    ViewerTabStatus.NeedsApproval => "？ " + tab.Title,
                    ViewerTabStatus.Generating => "… " + tab.Title,
                    _ => tab.Title,
                },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 4, 0),
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.FontSizeProperty, "Fs12");
            title.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "Fg" : "FgDim");

            var close = new Button { Content = "×", ToolTip = "このタブを閉じる", Margin = new Thickness(0, 0, 4, 0) };
            close.SetResourceReference(StyleProperty, "PaneHeaderTabClose");
            AutomationProperties.SetName(close, tab.Title + " を閉じる");
            close.Click += (_, e) =>
            {
                e.Handled = true;
                Close(tab);
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(title);
            row.Children.Add(close);
            var border = new Border
            {
                Child = row,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                ToolTip = tab.ToolTip,
            };
            if (isActive)
                border.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else
                border.BorderBrush = Brushes.Transparent;
            border.MouseLeftButtonDown += (_, _) => Activate(tab, persist: true);
            border.MouseDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    Close(tab);
                }
            };
            border.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenTabMenu(border, tab);
            };
            _strip.Children.Add(border);
        }
    }

    private void RenderCard()
    {
        UIElement? content = _openCardShown ? OpenPathCard()
            : _active is null ? EmptyCard()
            : _active.BuildCard();
        if (!_openCardShown)
        {
            _pathBox = null;
            _hint = null;
        }
        _card.Child = content is null ? null : new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _card.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement EmptyCard()
    {
        var panel = ViewerCards.Panel();
        panel.Children.Add(ViewerCards.Line("Viewer はまだ開いていません", "Fs14", "Fg", bold: true));
        panel.Children.Add(ViewerCards.Line(
            "ファイル・フォルダーのパスや URL を渡すと、Loomo が見せ方を決めてここに出します。HTML は頁のまま、PDF・画像・Markdown などは "
            + "EditorSupport と同じ表示、.exe は起動した窓をこのタブに重ねます、URL はブラウザと同じ中身です。見せ方の決まっていないファイルは "
            + "エディタに読むだけで出し、頼めば Claude Code が見せ方を作ります（同じ種類のファイルは次からそれで開きます）。",
            "Fs12", "FgDim"));
        panel.Children.Add(ViewerCards.Line("ファイルをここへドロップしても開けます。", "Fs12", "FgDim", top: 6));
        panel.Children.Add(ViewerCards.Buttons(
            ViewerCards.Button("パスを指定して開く…", "FlatButton", () => ShowOpenCard(), "ViewerOpenPathButton"),
            ViewerCards.Button("Viewer を生やす頼みごとの下書きをコピー", "SecondaryButton", () => GrowRequested?.Invoke(this, EventArgs.Empty))));
        return panel;
    }

    /// <summary>「パスを指定して開く」：入力欄・参照・どう出すかの 1 行・開く／やめる。Enter で開く、Esc でやめる。</summary>
    private UIElement OpenPathCard()
    {
        var panel = ViewerCards.Panel();
        panel.Children.Add(ViewerCards.Line("パスを指定して Viewer で開く", "Fs14", "Fg", bold: true));
        panel.Children.Add(ViewerCards.Line("ファイル・フォルダーのパスか URL（ドロップでも渡せます）", "Fs11", "FgDim", top: 8));
        var box = _pathBox ?? new TextBox { MinWidth = 360, Margin = new Thickness(0, 4, 0, 0) };
        if (box.Parent is Panel oldParent)
            oldParent.Children.Remove(box);
        AutomationProperties.SetAutomationId(box, "ViewerPathBox");
        AutomationProperties.SetName(box, "開くパス");
        if (!ReferenceEquals(box, _pathBox))
        {
            box.TextChanged += (_, _) =>
            {
                _hintTimer.Stop();
                _hintTimer.Start();
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    OpenPath(box.Text);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    HideOpenCard();
                }
            };
        }
        _pathBox = box;
        panel.Children.Add(box);
        var hint = _hint = ViewerCards.Line(" ", "Fs12", "FgDim", top: 6);
        AutomationProperties.SetAutomationId(hint, "ViewerPathHint");
        panel.Children.Add(hint);
        panel.Children.Add(ViewerCards.Buttons(
            ViewerCards.Button("開く", "FlatButton", () => OpenPath(box.Text), "ViewerOpenButton"),
            ViewerCards.Button("ファイルを選ぶ…", "SecondaryButton", BrowseFile),
            ViewerCards.Button("フォルダーを選ぶ…", "SecondaryButton", BrowseFolder),
            ViewerCards.Button("やめる", "SecondaryButton", HideOpenCard)));
        _ = UpdateHintAsync();
        return panel;
    }

    private void HideOpenCard()
    {
        if (!_openCardShown)
            return;
        _openCardShown = false;
        RenderCard();
    }

    private async Task UpdateHintAsync()
    {
        if (_factory is not { } factory || _pathBox is not { } box || _hint is not { } hint)
            return;
        var input = box.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            hint.Text = "Loomo が中身に合わせて見せ方を決めます";
            return;
        }
        if ((ViewerTargets.WebUrl(input) ?? ViewerTargets.Normalize(input)) is not { } path)
        {
            hint.Text = "フルパスか URL で入れてください（例：C:\\work\\report.pdf、https://example.com）";
            return;
        }
        var text = await Task.Run(() => factory.Describe(factory.Classify(path)));
        if (ReferenceEquals(_hint, hint) && box.Text == input)
            hint.Text = text;
    }

    private void BrowseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Viewer で開くファイル", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            OpenPath(dialog.FileName);
    }

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Viewer で開くフォルダー" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            OpenPath(dialog.FolderName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        e.Handled = true;
        foreach (var path in paths.Take(8))
            OpenPath(path);
    }

    /// <summary>タブの右クリック。</summary>
    private void OpenTabMenu(FrameworkElement anchor, IViewerTab tab)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        Item(menu, "閉じる", () => Close(tab));
        Item(menu, "他のタブを閉じる", () =>
        {
            foreach (var other in _tabs.Where(other => !ReferenceEquals(other, tab)).ToArray())
                Close(other);
        }, enabled: _tabs.Count > 1);
        menu.Items.Add(new Separator());
        Item(menu, "別ウィンドウで開く", () => DetachRequested?.Invoke(this, tab));
        if (tab is ViewerTabHost { CanRemake: true } host)
            Item(menu, "Viewer を作り直す…", () => Remake(host));
        if (tab is ViewerTabBase { CanGenerate: true } based)
            Item(menu, "Claude Code に見せ方を作ってもらう", based.RequestGenerate);
        if (tab is ViewerWebTab web)
        {
            menu.Items.Add(new Separator());
            Item(menu, "URL をコピー", () => ClipboardText.Set(web.Url));
        }
        if (tab.TargetPath is { } path)
        {
            menu.Items.Add(new Separator());
            Item(menu, "パスをコピー", () => ClipboardText.Set(path));
            Item(menu, "エクスプローラーで表示", () => FileExplorerLauncher.RevealNearestExisting(path));
        }
        menu.IsOpen = true;
    }

    private static void Item(ContextMenu menu, string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        menu.Items.Add(item);
    }

    /// <summary>「作り直す」：同じ場所に、一言を聞くカードのタブを入れる（やめれば元の Viewer に戻る）。</summary>
    private void Remake(ViewerTabHost host)
    {
        if (_factory is null)
            return;
        var remake = _factory.Remake(host, _contentHost);
        Replace(host, remake);
    }

    /// <summary>「＋」のメニュー。パスを開く・置いてある Viewer（枯れたものは下へ分ける）・頼みごと・フォルダー。押したときに組む。</summary>
    private void OpenMenu(Button anchor)
    {
        if (_factory is not { } factory)
            return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        Item(menu, "パスを指定して開く…", () => ShowOpenCard());
        menu.Items.Add(new Separator());
        var today = DateTime.Today;
        // ファイルごとに作った Viewer（file-…）は開くファイルが要るので、ここには並べない。
        var viewers = factory.Store.List().Where(entry => !ViewerPath.IsGenerated(entry.Id)).ToList();
        var withered = new List<ViewerEntry>();
        foreach (var entry in viewers)
        {
            if (IsWithered(entry, today) && _tabs.OfType<ViewerTabHost>().All(open => open.Id != entry.Id))
                withered.Add(entry);
            else
                menu.Items.Add(ViewerItem(entry, ""));
        }
        if (viewers.Count == 0)
            menu.Items.Add(new MenuItem { Header = "（置いてある Viewer はまだありません）", IsEnabled = false });
        if (withered.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = $"枯れた Viewer（{ViewerRoomState.WitherDays} 日開いていない）", IsEnabled = false });
            foreach (var entry in withered)
                menu.Items.Add(ViewerItem(entry, "　"));
        }
        menu.Items.Add(new Separator());
        Item(menu, "Viewer を生やす頼みごとの下書きをコピー", () => GrowRequested?.Invoke(this, EventArgs.Empty));
        Item(menu, "Viewer のフォルダーを開く", () =>
        {
            try
            {
                Directory.CreateDirectory(factory.Store.Root);
                Process.Start(new ProcessStartInfo(factory.Store.Root) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                ToastService.Error("Viewer のフォルダーを開けませんでした");
            }
        });
        menu.IsOpen = true;
    }

    private MenuItem ViewerItem(ViewerEntry entry, string indent)
    {
        var item = new MenuItem
        {
            Header = indent + (entry.Manifest is null ? "⚠ " + entry.Id : entry.Title),
            IsChecked = _tabs.OfType<ViewerTabHost>().Any(open => open.Id == entry.Id && open.TargetPath is null),
            ToolTip = entry.Manifest?.Description is { Length: > 0 } description ? description : entry.Error,
        };
        item.Click += (_, _) => _ = ShowViewerAsync(entry.Id);
        return item;
    }

    private bool IsWithered(ViewerEntry entry, DateTime today)
    {
        try
        {
            return _factory!.State.IsWithered(entry.Id, Directory.GetCreationTime(_factory.Store.FolderOf(entry.Id)), today);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
