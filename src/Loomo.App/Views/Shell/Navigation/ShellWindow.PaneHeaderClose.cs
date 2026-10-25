namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインヘッダーの見出し（見ているタブ）から閉じる。
/// タブ帯を外したペイン（Terminal / Editor / Browser）では見出しが「見ているタブ」そのものなので、
/// タブ帯と同じ手癖をここで受ける：ホバーで出る ×・中クリック・右クリックの閉じる操作。
/// 閉じ方は TABS と同じ経路（<see cref="CloseSidebarTabsAsync"/>）に流し、挙動を分けない。</summary>
public partial class ShellWindow {
    private async void OnPaneHeaderCloseTab(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: TabEntryViewModel tab })
            await CloseSidebarTabsAsync(tab, WorkspaceTabCloseScope.Selected);
    }

    private async void OnPaneHeaderTitleMouseDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { Tag: TabEntryViewModel tab })
            return;
        e.Handled = true;
        await CloseSidebarTabsAsync(tab, WorkspaceTabCloseScope.Selected);
    }

    private void OnPaneHeaderTitleRightClick(object sender, MouseButtonEventArgs e) {
        if (sender is not FrameworkElement { Tag: TabEntryViewModel tab } target)
            return;
        e.Handled = true;
        var ids = tab.Kind switch {
            TabEntryKind.Terminal => _terminalTabs.Select(t => t.Id).ToList(),
            TabEntryKind.Editor => _editorTabs.Select(t => t.Id).ToList(),
            TabEntryKind.Browser => _browserTabs.Select(t => t.Id).ToList(),
            _ => new List<Guid>(),
        };
        var count = ids.Count;
        var index = ids.IndexOf(tab.Id);
        var menu = new ContextMenu { PlacementTarget = target };
        AddPaneHeaderCloseItem(menu, "閉じる", tab, WorkspaceTabCloseScope.Selected, enabled: true);
        AddPaneHeaderCloseItem(menu, "他のタブを閉じる", tab, WorkspaceTabCloseScope.Others, enabled: count > 1);
        AddPaneHeaderCloseItem(menu, "これより後のタブを閉じる", tab, WorkspaceTabCloseScope.After, enabled: index >= 0 && index < count - 1);
        AddPaneHeaderCloseItem(menu, "これより前のタブを閉じる", tab, WorkspaceTabCloseScope.Before, enabled: index > 0);
        AddPaneHeaderCloseItem(menu, "すべて閉じる", tab, WorkspaceTabCloseScope.All, enabled: true);
        if (tab.CanRename) {
            menu.Items.Add(new Separator());
            var rename = new MenuItem { Header = "名前を付ける…" };
            rename.Click += (_, _) => RenameTerminalTab(tab.Id);
            menu.Items.Add(rename);
            var note = new MenuItem { Header = tab.HasNote ? "メモを書き直す…" : "メモ…", ToolTip = tab.Note };
            note.Click += (_, _) => EditTerminalTabNote(tab.Id);
            menu.Items.Add(note);
            var color = new MenuItem { Header = "色" };
            TerminalTabColors.FillMenu(color, tab.ColorKey, key => _vm.Tabs.SetTerminalTabColor(tab, key));
            menu.Items.Add(color);
        }
        if (tab.BrowserUrl is { Length: > 0 }) {
            menu.Items.Add(new Separator());
            var copyUrl = new MenuItem { Header = "URL をコピー" };
            copyUrl.Click += (_, _) => _vm.Tabs.CopyBrowserUrlCommand.Execute(tab);
            menu.Items.Add(copyUrl);
            var copyLink = new MenuItem { Header = "リンクをコピー（Markdown / HTML）" };
            copyLink.Click += (_, _) => _vm.Tabs.CopyBrowserLinkCommand.Execute(tab);
            menu.Items.Add(copyLink);
        }
        menu.IsOpen = true;
    }

    private void AddPaneHeaderCloseItem(
        ContextMenu menu, string header, TabEntryViewModel tab, WorkspaceTabCloseScope scope, bool enabled) {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += async (_, _) => await CloseSidebarTabsAsync(tab, scope);
        menu.Items.Add(item);
    }
}
