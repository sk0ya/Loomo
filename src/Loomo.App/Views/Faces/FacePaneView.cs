using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Faces;
using sk0ya.Loomo.Services.Faces;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 「面」ペインの中身（§44.2）。面 1 つ＝タブ 1 つ。上にタブの並びと「＋」（置いてある面の一覧・面のフォルダー・
/// 生やす頼みごとの下書き）、下に前に出ている面のページ。ページを出せないとき（まだ許していない・定義が読めない・
/// 面が 1 つも無い）は、同じ場所にカードを出す。
/// <para>読み込むのは<b>前に出ている面だけ</b>——復元したタブは選ばれるまでページを作らない。</para>
/// XAML を持たない（部品が少なく、色と字の大きさは部屋のトークンをそのまま引く）。
/// </summary>
internal sealed class FacePaneView : UserControl
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _webHost = new();
    private readonly Border _card = new() { Visibility = Visibility.Collapsed };
    private readonly List<FaceTabHost> _faces = [];
    private FaceTabHost? _active;
    private FaceStore? _store;
    private FaceRoomState? _state;
    private FaceRoomApi? _api;
    private IEditorSupportViewFactory? _factory;
    private Func<string, Task>? _openUrl;

    public FacePaneView()
    {
        Focusable = true;
        SetResourceReference(BackgroundProperty, "Panel");

        var add = new Button { Content = "＋", ToolTip = "面を開く・面のフォルダー・面を生やす", Width = 28, Padding = new Thickness(0) };
        add.SetResourceReference(StyleProperty, "PaneHeaderGhostButton");
        add.Click += (_, _) => OpenMenu(add);
        DockPanel.SetDock(add, Dock.Right);

        var strip = new DockPanel { LastChildFill = true, Height = 26 };
        strip.Children.Add(add);
        strip.Children.Add(new ScrollViewer
        {
            Content = _tabs,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var stripBorder = new Border { Child = strip, BorderThickness = new Thickness(0, 0, 0, 1) };
        stripBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(stripBorder, Dock.Top);

        var body = new Grid();
        body.Children.Add(_webHost);
        body.Children.Add(_card);
        _card.SetResourceReference(Border.BackgroundProperty, "Panel");

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(stripBorder);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>「面を生やす」を押した（頼みごとの下書きを渡すのは部屋の仕事）。</summary>
    public event EventHandler? GrowRequested;

    public IReadOnlyList<string> OpenIds => _faces.Select(face => face.Id).ToArray();

    public void Configure(FaceStore store, FaceRoomState state, FaceRoomApi api,
        IEditorSupportViewFactory factory, Func<string, Task> openUrl)
    {
        _store = store;
        _state = state;
        _api = api;
        _factory = factory;
        _openUrl = openUrl;
        // 前に開いていた面を戻す（数えない・前に出ていた 1 つだけ読み込む）。
        var (open, active) = state.OpenFaces;
        foreach (var id in open)
            _ = AddTab(id).PeekAsync();
        Activate(_faces.FirstOrDefault(face => face.Id == active) ?? _faces.FirstOrDefault(), persist: false);
        RenderTabs();
        RenderCard();
    }

    /// <summary>面をタブに出す（すでに出ていればそれを前へ）。定義が読めなくてもタブは出す——理由がカードに出る。</summary>
    public async Task<FaceTabHost?> ShowFaceAsync(string id)
    {
        if (_store is null || _state is null || !FaceManifest.IsValidId(id))
            return null;
        var face = _faces.FirstOrDefault(existing => existing.Id == id);
        if (face is null)
        {
            face = AddTab(id);
            _state.RecordOpened(id, DateTime.Today);
        }
        // 先に定義を読み直してから前へ出す（前へ出したときの読み込みと二重に走らせない）。
        await face.RefreshAsync();
        Activate(face, persist: true);
        return face;
    }

    /// <summary>部屋の出来事を、出ている面へ流す。</summary>
    public void Emit(string name, JsonNode? data, string requiredApi)
    {
        foreach (var face in _faces)
            face.Emit(name, data?.DeepClone(), requiredApi);
    }

    public void FocusActive()
    {
        if (_active is { Status: FaceTabStatus.Running } face)
            face.Focus();
        else
            Focus();
    }

    private FaceTabHost AddTab(string id)
    {
        var face = new FaceTabHost(id, _store!, _state!, _api!, _factory!, _webHost, Dispatcher, _openUrl!);
        face.Changed += (_, _) =>
        {
            RenderTabs();
            if (ReferenceEquals(face, _active))
                RenderCard();
        };
        _faces.Add(face);
        return face;
    }

    private void Activate(FaceTabHost? face, bool persist)
    {
        _active = face;
        foreach (var other in _faces)
            other.SetVisible(ReferenceEquals(other, face));
        if (face is { Status: FaceTabStatus.Pending })
            _ = face.RefreshAsync();
        RenderTabs();
        RenderCard();
        if (persist)
            Persist();
    }

    private void Close(FaceTabHost face)
    {
        var index = _faces.IndexOf(face);
        if (index < 0)
            return;
        _faces.RemoveAt(index);
        face.Dispose();
        if (ReferenceEquals(face, _active))
            Activate(_faces.Count == 0 ? null : _faces[Math.Min(index, _faces.Count - 1)], persist: false);
        else
            RenderTabs();
        Persist();
    }

    private void Persist() => _state?.SetOpenFaces(_faces.Select(face => face.Id), _active?.Id);

    // ── 見え方 ───────────────────────────────────────────────

    private void RenderTabs()
    {
        _tabs.Children.Clear();
        foreach (var face in _faces)
        {
            var isActive = ReferenceEquals(face, _active);
            var title = new TextBlock
            {
                Text = face.Status switch
                {
                    FaceTabStatus.Broken => "⚠ " + face.Title,
                    FaceTabStatus.NeedsApproval => "？ " + face.Title,
                    _ => face.Title,
                },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 4, 0),
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.FontSizeProperty, "Fs12");
            title.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "Fg" : "FgDim");

            var close = new Button { Content = "×", ToolTip = "この面を閉じる（面そのものは消えません）", Margin = new Thickness(0, 0, 4, 0) };
            close.SetResourceReference(StyleProperty, "PaneHeaderTabClose");
            close.Click += (_, e) =>
            {
                e.Handled = true;
                Close(face);
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(title);
            row.Children.Add(close);
            var tab = new Border
            {
                Child = row,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                ToolTip = face.Manifest?.Description is { Length: > 0 } description ? description : face.Id,
            };
            if (isActive)
                tab.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else
                tab.BorderBrush = Brushes.Transparent;
            tab.MouseLeftButtonDown += (_, _) => Activate(face, persist: true);
            _tabs.Children.Add(tab);
        }
    }

    private void RenderCard()
    {
        UIElement? content = _active switch
        {
            null => EmptyCard(),
            { Status: FaceTabStatus.NeedsApproval, Manifest: { } manifest } face => ApprovalCard(face, manifest),
            { Status: FaceTabStatus.Broken } face => BrokenCard(face),
            _ => null,
        };
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
        var panel = CardPanel();
        panel.Children.Add(Line("面はまだ出ていません", "Fs14", "Fg", bold: true));
        panel.Children.Add(Line(
            "面は、頼むと生える小さな道具です。ターミナルの Claude Code に「〜を一覧する面がほしい」のように頼むと、"
            + "Claude が小さな HTML を書いて、ここに出します。Loomo の再ビルドは要りません。", "Fs12", "FgDim"));
        var grow = new Button { Content = "面を生やす頼みごとの下書きをコピー", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        grow.SetResourceReference(StyleProperty, "SecondaryButton");
        grow.Click += (_, _) => GrowRequested?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(grow);
        if (_store?.List() is { Count: > 0 })
            panel.Children.Add(Line("置いてある面は、右上の「＋」から開けます。", "Fs12", "FgDim"));
        return panel;
    }

    private UIElement ApprovalCard(FaceTabHost face, FaceManifest manifest)
    {
        var panel = CardPanel();
        panel.Children.Add(Line($"面「{manifest.Title}」を開きますか？", "Fs14", "Fg", bold: true));
        if (manifest.Description.Length > 0)
            panel.Children.Add(Line(manifest.Description, "Fs12", "FgDim"));
        panel.Children.Add(Line("この面が部屋でできること", "Fs11", "FgDim", bold: true, top: 12));
        if (manifest.Api.Count == 0)
            panel.Children.Add(Line("・何もしない（自分のページを出すだけ）", "Fs12", "Fg"));
        foreach (var api in manifest.Api)
            panel.Children.Add(Line("・" + FaceApi.Describe(api), "Fs12", "Fg"));
        if (manifest.Commands.Count > 0)
        {
            panel.Children.Add(Line("回せるコマンド（この行のとおりにだけ走ります）", "Fs11", "FgDim", bold: true, top: 12));
            foreach (var command in manifest.Commands)
            {
                panel.Children.Add(Line(command.Name + (command.Cwd == FaceCommand.CwdFace ? "（面のフォルダーで）" : "（ワークスペースで）"),
                    "Fs11", "FgDim", top: 6));
                var line = Line(command.Command, "Fs12", "Fg");
                line.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                panel.Children.Add(line);
            }
        }
        panel.Children.Add(Line("口やコマンドが変わったら、もう一度ここで聞きます。ページの書き直しでは聞きません。", "Fs11", "FgDim", top: 12));

        var allow = new Button { Content = "許して開く", Margin = new Thickness(0, 0, 8, 0) };
        allow.SetResourceReference(StyleProperty, "FlatButton");
        allow.Click += (_, _) => _ = face.ApproveAsync();
        var dismiss = new Button { Content = "開かない" };
        dismiss.SetResourceReference(StyleProperty, "SecondaryButton");
        dismiss.Click += (_, _) => Close(face);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(allow);
        buttons.Children.Add(dismiss);
        panel.Children.Add(buttons);
        return panel;
    }

    private UIElement BrokenCard(FaceTabHost face)
    {
        var panel = CardPanel();
        panel.Children.Add(Line($"面「{face.Id}」を読めません", "Fs14", "Fg", bold: true));
        var reason = Line(face.Error ?? "", "Fs12", "Fg");
        reason.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        panel.Children.Add(reason);
        panel.Children.Add(Line("面のフォルダーの中身が直ると、ここは自動で読み直します。", "Fs11", "FgDim", top: 10));
        return panel;
    }

    private static StackPanel CardPanel() => new() { Margin = new Thickness(16, 14, 16, 14), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };

    private static TextBlock Line(string text, string size, string color, bool bold = false, double top = 2)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        block.SetResourceReference(TextBlock.FontSizeProperty, size);
        block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    /// <summary>「＋」のメニュー。置いてある面を全部（枯れたものは下へ分ける）。メニューは押したときに組む。</summary>
    private void OpenMenu(Button anchor)
    {
        if (_store is null || _state is null)
            return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var today = DateTime.Today;
        var faces = _store.List();
        var withered = new List<FaceEntry>();
        foreach (var entry in faces)
        {
            if (IsWithered(entry, today) && _faces.All(open => open.Id != entry.Id))
                withered.Add(entry);
            else
                menu.Items.Add(FaceItem(entry, ""));
        }
        if (faces.Count == 0)
            menu.Items.Add(new MenuItem { Header = "（面はまだありません）", IsEnabled = false });
        if (withered.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = $"枯れた面（{FaceRoomState.WitherDays} 日開いていない）", IsEnabled = false });
            foreach (var entry in withered)
                menu.Items.Add(FaceItem(entry, "　"));
        }
        menu.Items.Add(new Separator());
        var grow = new MenuItem { Header = "面を生やす頼みごとの下書きをコピー" };
        grow.Click += (_, _) => GrowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(grow);
        var folder = new MenuItem { Header = "面のフォルダーを開く" };
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_store.Root);
                Process.Start(new ProcessStartInfo(_store.Root) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                ToastService.Error("面のフォルダーを開けませんでした");
            }
        };
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private MenuItem FaceItem(FaceEntry entry, string indent)
    {
        var item = new MenuItem
        {
            Header = indent + (entry.Manifest is null ? "⚠ " + entry.Id : entry.Title),
            IsChecked = _faces.Any(open => open.Id == entry.Id),
            ToolTip = entry.Manifest?.Description is { Length: > 0 } description ? description : entry.Error,
        };
        item.Click += (_, _) => _ = ShowFaceAsync(entry.Id);
        return item;
    }

    private bool IsWithered(FaceEntry entry, DateTime today)
    {
        try
        {
            return _state!.IsWithered(entry.Id, Directory.GetCreationTime(_store!.FolderOf(entry.Id)), today);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
