using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var node in Nodes) ExpandRecursive(node, depth: 0);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes) CollapseRecursive(node);
    }

    /// <summary>1つのフォルダーの配下だけを展開する（ツールバーの「すべて展開」と同じ深さ制限・
    /// 再解析ポイント除外を使う）。ツリー全体を開かずに済むよう、右クリックからの入口を分けたもの。</summary>
    public void ExpandSubtree(FileNodeViewModel node) => ExpandRecursive(node, depth: 0);

    /// <summary>1つのフォルダーの配下だけを折りたたむ。そのフォルダー自身は開いたままにする
    /// （名前どおり「配下」だけを畳み、直下の子は見えたままにする）。</summary>
    public void CollapseSubtree(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        foreach (var child in node.Children) CollapseRecursive(child);
    }

    private static void ExpandRecursive(FileNodeViewModel node, int depth)
    {
        if (!node.IsDirectory || depth > FolderTreeFilter.MaxDepth || FolderTreeFilter.IsReparsePoint(node.FullPath))
            return;
        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandRecursive(child, depth + 1);
    }

    private static void CollapseRecursive(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        node.IsExpanded = false;
        foreach (var child in node.Children) CollapseRecursive(child);
    }
}
