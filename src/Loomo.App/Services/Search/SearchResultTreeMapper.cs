using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>検索結果をフォルダー階層の表示モデルへ変換する Mapper。</summary>
public sealed class SearchResultTreeMapper
{
    public IReadOnlyList<object> Map(IReadOnlyList<SearchFileGroup> groups)
    {
        var root = new SearchFolderNode("", "");
        var cache = new Dictionary<string, SearchFolderNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var group in groups)
            EnsureFolder(group.FolderPath, cache, root).Children.Add(group);
        for (var i = 0; i < root.Children.Count; i++)
            if (root.Children[i] is SearchFolderNode folder)
                root.Children[i] = CompactFolder(folder);
        SortChildren(root);
        return root.Children.ToList();
    }

    private static SearchFolderNode EnsureFolder(
        string folderPath, Dictionary<string, SearchFolderNode> cache, SearchFolderNode root)
    {
        if (string.IsNullOrEmpty(folderPath)) return root;
        if (cache.TryGetValue(folderPath, out var existing)) return existing;
        var slash = folderPath.LastIndexOf('/');
        var parentPath = slash >= 0 ? folderPath[..slash] : "";
        var name = slash >= 0 ? folderPath[(slash + 1)..] : folderPath;
        var parent = EnsureFolder(parentPath, cache, root);
        var node = new SearchFolderNode(name, folderPath);
        parent.Children.Add(node);
        cache[folderPath] = node;
        return node;
    }

    private static SearchFolderNode CompactFolder(SearchFolderNode node)
    {
        for (var i = 0; i < node.Children.Count; i++)
            if (node.Children[i] is SearchFolderNode child)
                node.Children[i] = CompactFolder(child);
        if (node.Children.Count != 1 || node.Children[0] is not SearchFolderNode only) return node;
        only.PrependName(node.Name);
        return only;
    }

    private static void SortChildren(SearchFolderNode node)
    {
        var ordered = node.Children
            .OrderBy(child => child is SearchFileGroup)
            .ThenBy(child => child switch
            {
                SearchFolderNode folder => folder.Name,
                SearchFileGroup group => group.FileName,
                _ => "",
            }, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (var child in ordered) node.Children.Add(child);
        foreach (var folder in node.Children.OfType<SearchFolderNode>()) SortChildren(folder);
    }
}
