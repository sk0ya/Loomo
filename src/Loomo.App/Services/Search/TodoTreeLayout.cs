namespace sk0ya.Loomo.App.Services;

/// <summary>絶対パスでフォルダーを識別し、複数ルートの同名ファイルを混ぜずにツリーへ並べる。</summary>
public static class TodoTreeLayout
{
    public static IReadOnlyList<object> Build(IEnumerable<TodoGroup> groups, IWorkspaceService workspace)
    {
        var roots = new List<object>();
        var folders = new Dictionary<string, TodoFolder>(StringComparer.OrdinalIgnoreCase);
        var duplicateRoots = workspace.Folders.GroupBy(RootName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var root = workspace.FolderFor(group.FullPath);
            var relative = root is null ? group.Name : Path.GetRelativePath(root, group.FullPath);
            var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            IList<object> children = roots;
            var parentPath = root ?? "";
            if (root is not null && workspace.Folders.Count > 1)
            {
                var title = duplicateRoots.Contains(RootName(root)) ? root : RootName(root);
                children = EnsureFolder(title, root, children).Children;
            }
            foreach (var part in parts.SkipLast(1))
            {
                parentPath = Path.Combine(parentPath, part);
                children = EnsureFolder(part, parentPath, children).Children;
            }
            children.Add(group);
        }
        for (var i = 0; i < roots.Count; i++)
            if (roots[i] is TodoFolder folder)
                roots[i] = Compact(folder, preserveRoot: workspace.Folders.Count > 1);
        Sort(roots);
        return roots;

        TodoFolder EnsureFolder(string title, string path, IList<object> parent)
        {
            if (folders.TryGetValue(path, out var found)) return found;
            var folder = new TodoFolder(title, path);
            folders[path] = folder;
            parent.Add(folder);
            return folder;
        }
    }

    /// <summary>直下がフォルダー1つだけの区間を1行へまとめる。複数ルートの境界は残す。</summary>
    private static TodoFolder Compact(TodoFolder folder, bool preserveRoot = false)
    {
        for (var i = 0; i < folder.Children.Count; i++)
            if (folder.Children[i] is TodoFolder child)
                folder.Children[i] = Compact(child);
        if (preserveRoot || folder.Children.Count != 1 || folder.Children[0] is not TodoFolder only)
            return folder;
        var compact = new TodoFolder($"{folder.Name} / {only.Name}", only.FullPath,
            folder.Paths.Concat(only.Paths).ToList());
        foreach (var child in only.Children) compact.Children.Add(child);
        return compact;
    }

    private static string RootName(string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var name = Path.GetFileName(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    private static void Sort(IList<object> items)
    {
        var sorted = items.OrderBy(item => item is TodoFolder ? 0 : 1)
            .ThenBy(item => item is TodoFolder folder ? folder.Name : ((TodoGroup)item).Title,
                StringComparer.OrdinalIgnoreCase).ToList();
        items.Clear();
        foreach (var item in sorted)
        {
            if (item is TodoFolder folder) Sort(folder.Children);
            items.Add(item);
        }
    }

    public static IEnumerable<TodoFolder> Folders(IEnumerable<object> items)
        => items.OfType<TodoFolder>().SelectMany(folder => new[] { folder }.Concat(Folders(folder.Children)));

    public static IEnumerable<TodoEntry> Entries(IEnumerable<object> items)
        => items.SelectMany(item => item switch {
            TodoFolder folder => Entries(folder.Children),
            TodoGroup group => group.Entries,
            _ => Enumerable.Empty<TodoEntry>(),
        });

    /// <summary>対象行までの祖先を返す。巡回移動ではこの順で展開・実体化する。</summary>
    public static IReadOnlyList<object> PathTo(IEnumerable<object> items, TodoEntry entry)
    {
        foreach (var item in items)
        {
            if (item is TodoGroup group && group.Entries.Contains(entry)) return [group, entry];
            if (item is TodoFolder folder && PathTo(folder.Children, entry) is { Count: > 0 } rest)
                return new object[] { folder }.Concat(rest).ToList();
        }
        return [];
    }
}
