namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: 部屋のタブを最近使った順に巡る（Ctrl+Tab・§24.65）。エディタ・ターミナル・ブラウザを区別せずに 1 本に並べ、
/// コマンドパレットに出す（ファイルのタブは右の欄に中身が出る）。Ctrl を押したまま Tab で進み（Shift で戻る）、Ctrl を離したところで
/// そのタブへ移る。Esc でやめる。パレットの中で打てば絞り込める——そのときは Enter で決める。</summary>
public partial class ShellWindow {
    private readonly RoomTabMru _tabMru = new();

    /// <summary>いまパレットに出している巡回の一覧。パレットが別の一覧に替わったら、Ctrl を離しても何もしない。</summary>
    private IReadOnlyList<PaletteCommand>? _tabSwitcherItems;

    /// <summary>修飾キーを押したまま開いた（離したら決める）。修飾キーの無いキーへ割り当て直したなら Enter で決める。</summary>
    private bool _tabSwitcherHolding;

    private bool IsTabSwitcherShown
        => IsPaletteOpen && _tabSwitcherItems is { } items && ReferenceEquals(_paletteCommands, items);

    private void TouchTabMru(TabEntryKind kind, Guid id) => _tabMru.Touch(new RoomTabRef(kind, id));

    /// <summary>焦点がタブを持つペインへ入った＝そのペインの今のタブを使った。</summary>
    private void TouchTabMruForPane(PaneKind pane) {
        switch (pane) {
            case PaneKind.Terminal when _activeTerminalTab is { } terminal:
                TouchTabMru(TabEntryKind.Terminal, terminal.Id);
                break;
            case PaneKind.Editor when _activeEditorTab is { } editor:
                TouchTabMru(TabEntryKind.Editor, editor.Id);
                break;
            case PaneKind.Browser when _activeBrowserTab is { } browser:
                TouchTabMru(TabEntryKind.Browser, browser.Id);
                break;
        }
    }

    /// <summary>巡回を開く。開いている最中なら選択を 1 つ動かす。1 行目は今のタブなので、開いたときは 2 行目（直前のタブ）を選ぶ。</summary>
    private void CycleRoomTabs(int direction) {
        if (IsTabSwitcherShown) {
            MovePaletteSelection(direction);
            return;
        }
        var items = BuildTabSwitcherItems();
        if (items.Count == 0)
            return;
        _tabSwitcherItems = items;
        _tabSwitcherHolding = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        OpenCommandPalette(PaletteMode.Command, items);
        if (items.Count > 1)
            MovePaletteSelection(direction);
    }

    /// <summary>修飾キーを全部離したら、選んでいるタブへ移る。</summary>
    private void OnTabSwitcherKeyUp(object sender, KeyEventArgs e) {
        if (!_tabSwitcherHolding)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;
        _tabSwitcherHolding = false;
        if (!IsTabSwitcherShown)
            return;
        e.Handled = true;
        ExecutePaletteSelection();
    }

    private IReadOnlyList<PaletteCommand> BuildTabSwitcherItems() {
        var live = new List<(RoomTabRef Ref, DateTime Seen, PaletteCommand Item)>();
        foreach (var tab in _terminalTabs)
            if (_vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tab.Id) is { } entry)
                live.Add((new(TabEntryKind.Terminal, tab.Id), tab.LastSeenUtc, RoomTabSwitcherItems.Terminal(entry, tab.View.WorkingDirectory, Activate(entry))));
        foreach (var tab in _editorTabs)
            if (_vm.Tabs.EditorTabs.FirstOrDefault(t => t.Id == tab.Id) is { } entry)
                live.Add((new(TabEntryKind.Editor, tab.Id), tab.LastSeenUtc,
                    RoomTabSwitcherItems.Editor(entry, tab.PeekFilePath, _workspace.ToDisplayPath, Activate(entry))));
        foreach (var tab in _browserTabs)
            if (_vm.Tabs.BrowserTabs.FirstOrDefault(t => t.Id == tab.Id) is { } entry)
                live.Add((new(TabEntryKind.Browser, tab.Id), tab.LastSeenUtc, RoomTabSwitcherItems.Browser(entry, Activate(entry))));
        return _tabMru.Order(live, x => x.Ref, x => x.Seen).Select(x => x.Item).ToList();

        // TABS で選んだときと同じ経路（ペインを出す・エディタは EditorSupport の都合を見る）に流し、焦点もそのペインへ。
        Action Activate(TabEntryViewModel entry) => () => {
            OnSidebarTabActivated(this, entry);
            FocusPane(entry.Kind switch {
                TabEntryKind.Terminal => PaneKind.Terminal,
                TabEntryKind.Browser => PaneKind.Browser,
                _ => PaneKind.Editor,
            });
        };
    }
}
