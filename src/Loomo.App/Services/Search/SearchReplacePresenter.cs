using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>検索結果の置換確認・通知と、置換用コンテキストメニューの表示状態を整える。</summary>
internal static class SearchReplacePresenter
{
    public static void ReplaceInFile(Window? owner, SearchPanelViewModel vm, SearchFileGroup group)
    {
        if (group.Count == 0)
            return;

        var confirm = MessageBox.Show(
            owner,
            $"「{group.FileName}」内の {group.Count} 件を置換しますか？\nこの操作は元に戻せません。",
            "置換の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.OK)
            vm.ReplaceInFile(group);
    }

    public static void PrepareFileMenu(ContextMenu menu, SearchPanelViewModel vm)
    {
        var group = (menu.PlacementTarget as FrameworkElement)?.DataContext as SearchFileGroup;
        SetTaggedVisibility(menu, "ReplaceFileMenu", vm.IsReplaceVisible && group is { Count: > 0 });
    }

    public static void ReplaceFromContextMenu(object sender, SearchPanelViewModel? vm)
    {
        if (vm is not null
            && sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: SearchMatchItem match } } })
            ReplaceOne(vm, match);
    }

    public static void PrepareMatchMenu(ContextMenu menu, SearchPanelViewModel vm)
        => SetTaggedVisibility(menu, "ReplaceOneMenu", vm.IsReplaceVisible);

    public static bool ReplaceOne(SearchPanelViewModel vm, SearchMatchItem match)
    {
        if (vm.ReplaceOne(match))
            return true;
        ToastService.Error("置換できませんでした（内容が変わった可能性があります）");
        return false;
    }

    public static bool ReplaceAll(Window? owner, SearchPanelViewModel vm)
    {
        var groups = vm.AllFileGroups();
        var matchCount = groups.Sum(group => group.Count);
        if (matchCount == 0)
            return false;

        var confirm = MessageBox.Show(
            owner,
            $"{groups.Count} ファイルの {matchCount} 件を置換しますか？\nこの操作は元に戻せません。",
            "置換の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return false;

        var (files, matches) = vm.ReplaceAll();
        ToastService.Success($"{files} ファイルの {matches} 件を置換しました。");
        return true;
    }

    private static void SetTaggedVisibility(ContextMenu menu, string tag, bool visible)
    {
        foreach (var item in menu.Items)
            if (item is MenuItem { Tag: var itemTag } menuItem && Equals(itemTag, tag))
                menuItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
