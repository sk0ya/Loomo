namespace sk0ya.Loomo.App.Views;

/// <summary>意味的な選択のWPF入口。選択範囲の連鎖と拡大・縮小はServices側へ委譲する。</summary>
public partial class ShellWindow
{
    private readonly SemanticSelectionController _semanticSelection = new();

    private void ExpandSemanticSelection() => _ = RunSemanticSelectionAsync(expand: true);
    private void ShrinkSemanticSelection() => _ = RunSemanticSelectionAsync(expand: false);

    private Task RunSemanticSelectionAsync(bool expand)
        => _semanticSelection.RunAsync(
            expand, FocusedEditorControl,
            message => EditorSharedStatusBar?.UpdateStatus(message));

    /// <summary>フォーカス要素から対象のEditor controlを得る。分割ビューではフォーカス中のタブを優先する。</summary>
    private VimEditorControl? FocusedEditorControl()
    {
        for (var node = Keyboard.FocusedElement as DependencyObject; node is not null; node = AnyParent(node))
            if (node is VimEditorControl focused) return focused;

        if (_focusedRegion is not { Pane: PaneKind.Editor }) return null;
        if (_editorViews?.FocusedTabId is { } id
            && _editorTabs.FirstOrDefault(t => t.Id == id) is { IsRealized: true } tab)
            return tab.Control;
        return _activeEditorTab is { IsRealized: true } active ? active.Control : null;

        static DependencyObject? AnyParent(DependencyObject d)
            => d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
    }

    /// <summary>右クリックメニューへ拡大／縮小を足す。「縮小」は戻れる段があるときだけ出す。</summary>
    private void AddSemanticSelectionMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (control?.LspDocument is not { IsReady: true, ServerSupportsSelectionRange: true } document) return;

        var canShrink = _semanticSelection.CanShrink(control, document);
        var expand = new MenuItem
        {
            Header = "選択を意味的に広げる",
            InputGestureText = DescribeBinding("editor.selection.expand"),
        };
        expand.Click += (_, _) => _ = RunSemanticSelectionAsync(expand: true);
        menu.Items.Add(expand);

        if (!canShrink) return;
        var shrink = new MenuItem
        {
            Header = "選択を1段戻す",
            InputGestureText = DescribeBinding("editor.selection.shrink"),
        };
        shrink.Click += (_, _) => _ = RunSemanticSelectionAsync(expand: false);
        menu.Items.Add(shrink);
    }

    private string DescribeBinding(string commandId)
        => _keybindings.Effective.TryGetValue(commandId, out var sequence) ? sequence.ToString() : "";
}
