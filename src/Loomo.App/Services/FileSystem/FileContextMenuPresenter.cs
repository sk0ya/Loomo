using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイルメニューの表示状態と FolderTree のメニュー操作 UI を管理する。</summary>
internal static class FileContextMenuPresenter
{
    /// <summary>FolderTree の Quick Access 操作中はカーソルを切り替え、バッチ失敗を通知する。</summary>
    public static async Task SetFolderTreeQuickAccessPinnedAsync(
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection,
        bool pin)
    {
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var result = await FolderTreeFileCommandController.TrySetQuickAccessPinnedAsync(vm, selection, pin);
            if (result is { HasFailures: true })
                ToastService.Error(result.ErrorMessage ?? (pin
                    ? "クイックアクセスへのピン留めに失敗しました。"
                    : "クイックアクセスからの解除に失敗しました。"));
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>ツリー空き領域のメニューにルート制約とファイル操作履歴を反映する。</summary>
    public static void PrepareFolderTreeBackgroundMenu(ContextMenu menu, FolderTreeViewModel? vm)
    {
        var allowed = vm is not null && !FolderTreeShellNamespaces.IsShellPath(vm.CurrentRoot);
        SetTaggedVisibility(menu, "FileSystemOnly", allowed);
        UpdateHistoryActions(menu, vm?.History.UndoDescription, vm?.History.RedoDescription);
        NormalizeSeparators(menu);
    }

    public static IEnumerable<MenuItem> Descendants(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Descendants(item))
                yield return child;
        }
    }

    public static void UpdateHistoryActions(
        ItemsControl menu, string? undoDescription, string? redoDescription)
    {
        foreach (var item in Descendants(menu))
            switch (item.Tag as string)
            {
                case "UndoItem": ApplyHistoryHeader(item, "元に戻す", undoDescription); break;
                case "RedoItem": ApplyHistoryHeader(item, "やり直す", redoDescription); break;
            }
    }

    public static void SetTaggedVisibility(ItemsControl menu, string tag, bool visible)
    {
        foreach (var item in Descendants(menu))
            if (item.Tag as string == tag)
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public static void PrepareFolderTreeMenu(
        ContextMenu menu,
        bool fileSystemVisible,
        bool showAi,
        bool showTwoFileDiff,
        string? undoDescription,
        string? redoDescription)
    {
        SetTaggedVisibility(menu, "FileSystemOnly", fileSystemVisible);
        SetTaggedVisibility(menu, "AiMenu", showAi);
        if (FindMenuItem(menu, "DiffMenu") is { } diffMenu
            && FindMenuItem(diffMenu, "CompareTwo") is { } compareTwo)
            compareTwo.Visibility = showTwoFileDiff ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryActions(menu, undoDescription, redoDescription);
        NormalizeMenuAndSubmenus(menu);
    }

    /// <summary>フォルダーツリーの選択に応じて動的な項目を作り、メニュー全体の表示を整える。</summary>
    public static void PrepareFolderTreeMenu(
        ContextMenu menu,
        FolderTreeViewModel? vm,
        FileNodeViewModel? node,
        IReadOnlyList<FileNodeViewModel> selection)
    {
        var showAi = vm is { IsAiReady: true }
            && FileContextMenuPolicy.ShouldShowAiMenu(
                true, selection.Count, selection.Any(item => !item.IsShellItem));

        if (showAi && node is { IsDirectory: false } && File.Exists(node.FullPath))
            PopulateWorkflowMenu(menu, vm!, node);

        if (vm is not null)
        {
            UpdateFolderTreeQuickAccessItems(menu, vm, selection);
            if (node is { IsWorkspaceFolderRoot: true })
                PopulateRootSwitchMenu(menu, vm, node);
        }

        PrepareFolderTreeMenu(
            menu,
            node is null || !node.IsShellItem,
            showAi,
            FileContextMenuPolicy.HasExactlyTwoFiles(selection.Count(item => !item.IsDirectory)),
            vm?.History.UndoDescription,
            vm?.History.RedoDescription);
    }

    /// <summary>ファイル一覧の状態と履歴をメニューへ反映する。</summary>
    public static void PrepareFilesColumnMenu(
        ContextMenu menu,
        FileContextMenuState state,
        string? undoDescription,
        string? redoDescription)
    {
        foreach (var item in Descendants(menu))
        {
            var visible = FileContextMenuPolicy.IsVisible(
                item.Tag as string, state, item.Visibility == Visibility.Visible);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateHistoryActions(menu, undoDescription, redoDescription);
        NormalizeMenuAndSubmenus(menu);
    }

    /// <summary>Explorer照会後、メニューが開いたままならピン留め項目を更新する。</summary>
    public static void UpdateFilesColumnQuickAccessItems(
        ContextMenu menu,
        FilesColumnViewModel vm,
        IReadOnlyList<FileEntryViewModel> selection,
        FilesColumnCommandController commands,
        bool quickAccessReady)
    {
        if (quickAccessReady || !vm.QuickAccess.IsAvailable
            || !selection.Any(entry => entry.IsDirectory))
            return;

        _ = RefreshFilesColumnQuickAccessItemsAsync(menu, vm, selection, commands);
    }

    private static async Task RefreshFilesColumnQuickAccessItemsAsync(
        ContextMenu menu,
        FilesColumnViewModel vm,
        IReadOnlyList<FileEntryViewModel> selection,
        FilesColumnCommandController commands)
    {
        var state = await commands.RefreshQuickAccessMenuStateAsync(vm, selection);
        if (state is not { } refreshed || !menu.IsOpen)
            return;

        SetTaggedVisibility(menu, "QuickAccessPinnable", refreshed.CanPin);
        SetTaggedVisibility(menu, "QuickAccessUnpinnable", refreshed.CanUnpin);
        NormalizeMenuAndSubmenus(menu);
    }

    private static void UpdateFolderTreeQuickAccessItems(
        ContextMenu menu,
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection)
    {
        var pin = FindMenuItem(menu, "QuickAccessPinnable");
        var unpin = FindMenuItem(menu, "QuickAccessUnpinnable");
        if (pin is null && unpin is null)
            return;

        var state = FolderTreeFileCommandController.GetQuickAccessMenuState(vm, selection);
        if (state.ReadyState is { } ready)
        {
            SetVisibility(pin, ready.CanPin);
            SetVisibility(unpin, ready.CanUnpin);
            return;
        }

        SetVisibility(pin, false);
        SetVisibility(unpin, false);
        if (state.HasTarget)
            _ = RefreshFolderTreeQuickAccessItemsAsync(menu, vm, selection, pin, unpin);
    }

    private static async Task RefreshFolderTreeQuickAccessItemsAsync(
        ContextMenu menu,
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection,
        MenuItem? pin,
        MenuItem? unpin)
    {
        var state = await FolderTreeFileCommandController.RefreshQuickAccessMenuStateAsync(vm, selection);
        if (state is not { } refreshed || !menu.IsOpen)
            return;

        SetVisibility(pin, refreshed.CanPin);
        SetVisibility(unpin, refreshed.CanUnpin);
        NormalizeMenuAndSubmenus(menu);
    }

    private static void PopulateRootSwitchMenu(
        ContextMenu menu, FolderTreeViewModel vm, FileNodeViewModel headerNode)
    {
        var switchMenu = FindMenuItem(menu, "RootSwitchMenu");
        if (switchMenu is null)
            return;

        var options = vm.RootOptionsFor(headerNode);
        var selected = vm.SelectedRootOptionFor(headerNode);
        switchMenu.Items.Clear();
        foreach (var option in options)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = ReferenceEquals(option, selected),
            };
            item.Click += (_, _) => vm.SwitchRootOption(headerNode, option);
            switchMenu.Items.Add(item);
        }
    }

    private static void PopulateWorkflowMenu(
        ContextMenu menu, FolderTreeViewModel vm, FileNodeViewModel node)
    {
        var aiMenu = FindMenuItem(menu, "AiMenu");
        var submenu = aiMenu is null ? null : FindMenuItem(aiMenu, "AiWorkflowMenu");
        if (submenu is null)
            return;

        var workflows = vm.InputWorkflows();
        submenu.Visibility = workflows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        submenu.Items.Clear();
        foreach (var workflow in workflows)
        {
            var id = workflow.Id;
            var item = new MenuItem { Header = workflow.Name };
            item.Click += (_, _) => vm.RequestRunWorkflow(node, id);
            submenu.Items.Add(item);
        }
    }

    public static void NormalizeMenuAndSubmenus(ItemsControl menu)
    {
        foreach (var submenu in menu.Items.OfType<MenuItem>())
        {
            NormalizeMenuAndSubmenus(submenu);
            // 用途別グループは中身がないとき隠す。再表示時と非同期のピン照会でも再評価する。
            if (submenu.Tag as string == "AutoGroup")
                submenu.Visibility = submenu.Items.OfType<MenuItem>().Any(item => item.Visibility == Visibility.Visible)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        NormalizeSeparators(menu);
    }

    /// <summary>表示中の項目の間にある区切り線だけを残す。</summary>
    public static void NormalizeSeparators(ItemsControl menu)
    {
        Separator? pending = null;
        var sawVisibleItem = false;

        foreach (var item in menu.Items)
        {
            if (item is Separator separator)
            {
                separator.Visibility = Visibility.Collapsed;
                pending = sawVisibleItem ? separator : null;
                continue;
            }

            if (item is not FrameworkElement { Visibility: Visibility.Visible })
                continue;

            sawVisibleItem = true;
            if (pending is not null)
            {
                pending.Visibility = Visibility.Visible;
                pending = null;
            }
        }
    }

    private static void ApplyHistoryHeader(MenuItem item, string verb, string? description)
    {
        var presentation = FileContextMenuPolicy.FormatHistoryAction(verb, description);
        item.Visibility = presentation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        item.Header = presentation.Header;
    }

    private static MenuItem? FindMenuItem(ItemsControl menu, string tag)
        => Descendants(menu).FirstOrDefault(item => item.Tag as string == tag);

    private static void SetVisibility(UIElement? element, bool visible)
    {
        if (element is not null)
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
