using sk0ya.Loomo.CSharp.Editor;
using System.Windows.Automation;

namespace sk0ya.Loomo.App.Services;

/// <summary>C# エディタの右クリックメニューを組み立てる。</summary>
internal static class CSharpEditorContextMenuBuilder
{
    internal static void AddMenuItems(
        ContextMenu menu,
        VimEditorControl? control,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCommand,
        Action<MenuItem, string> addFixAllMenuItems)
    {
        if (control?.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            return;

        var root = new MenuItem { Header = "C#" };
        SetAutomation(root, "CSharpMenu");

        var plan = CSharpEditorMenu.Build(control.HasSelection);
        AddSections(root, control, plan.Primary, gestureFor, executeCommand);
        AddSubmenu(root, control, "書き換え", "CSharpMoreRewrite", plan.MoreRewrite,
            gestureFor, executeCommand);
        AddSubmenu(root, control, "生成", "CSharpMoreGenerate", plan.MoreGenerate,
            gestureFor, executeCommand);
        AddTidySubmenu(root, control, plan.Tidy, path, gestureFor, executeCommand,
            addFixAllMenuItems);

        if (root.Items.Count > 0)
            menu.Items.Add(root);
    }

    internal static void AddPeekMenuItem(
        MenuItem? navigateMenu,
        VimEditorControl? control,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCommand)
    {
        if (navigateMenu is null || control?.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            return;
        AddCommandItem(navigateMenu, control, new CSharpMenuEntry(
            CSharpEditorCommandCatalog.PeekDefinition,
            CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.PeekDefinition),
            CSharpEditorMenu.GestureFor(CSharpEditorCommandCatalog.PeekDefinition)),
            gestureFor, executeCommand);
    }

    private static void AddSubmenu(
        MenuItem root, VimEditorControl control, string header, string automationId,
        IReadOnlyList<CSharpMenuEntry> entries,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCommand)
    {
        if (entries.Count == 0)
            return;
        var child = new MenuItem { Header = header };
        SetAutomation(child, automationId);
        foreach (var entry in entries)
            AddCommandItem(child, control, entry, gestureFor, executeCommand);
        AddSeparatorBeforeTail(root);
        root.Items.Add(child);
    }

    private static void AddTidySubmenu(
        MenuItem root, VimEditorControl control,
        IReadOnlyList<CSharpMenuEntry> entries, string filePath,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCommand,
        Action<MenuItem, string> addFixAllMenuItems)
    {
        var child = new MenuItem { Header = "まとめて整える" };
        SetAutomation(child, "CSharpTidy");
        foreach (var entry in entries)
            AddCommandItem(child, control, entry, gestureFor, executeCommand);
        addFixAllMenuItems(child, filePath);
        if (child.Items.Count == 0)
            return;
        AddSeparatorBeforeTail(root);
        root.Items.Add(child);
    }

    private static void AddSeparatorBeforeTail(MenuItem root)
    {
        if (root.Items.Count > 0 && root.Items[^1] is MenuItem { HasItems: false })
            root.Items.Add(new Separator());
    }

    private static void AddSections(
        MenuItem root, VimEditorControl control,
        IReadOnlyList<CSharpMenuSection> sections,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCommand)
    {
        foreach (var section in sections)
        {
            if (section.Entries.Count == 0)
                continue;
            if (root.Items.Count > 0)
                root.Items.Add(new Separator());
            foreach (var entry in section.Entries)
                AddCommandItem(root, control, entry, gestureFor, executeCommand);
        }
    }

    private static void AddCommandItem(
        MenuItem root, VimEditorControl control, CSharpMenuEntry entry,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCommand)
    {
        var item = new MenuItem
        {
            Header = entry.Header,
            InputGestureText = gestureFor(entry.CommandId),
            Tag = entry.CommandId,
        };
        SetAutomation(item, entry.CommandId);
        item.Click += (_, _) => executeCommand(entry.CommandId, control);
        root.Items.Add(item);
    }

    private static void SetAutomation(MenuItem item, string id)
    {
        AutomationProperties.SetAutomationId(item, id);
        AutomationProperties.SetName(item, item.Header?.ToString() ?? id);
    }
}
