using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: キーボードショートカットの結線。<see cref="CommandCatalog"/> の各コマンド Id を 実体アクションへ結び、<see cref="KeyboardDispatcher"/> を組み立てる。ディスパッチャは <see cref="KeybindingService"/> から実効バインドを得るので、設定画面での再割り当てが即反映される。 新しいショートカットは、カタログに 1 行足してここへアクションを 1 行結ぶだけで有効になる。</summary>
public partial class ShellWindow {
    private KeyboardDispatcher BuildKeyboardDispatcher() {
        var actions = new Dictionary<string, Action>(StringComparer.Ordinal) {
            ["palette.open"] = OpenCommandPalette, ["palette.openFromPrefix"] = OpenCommandPalette,
            ["pane.focus.left"] = () => FocusPaneInDirection(DropZone.Left), ["pane.focus.down"] = () => FocusPaneInDirection(DropZone.Below), ["pane.focus.up"] = () => FocusPaneInDirection(DropZone.Above), ["pane.focus.right"] = () => FocusPaneInDirection(DropZone.Right),
            ["pane.resize.left"] = () => ResizeFocusedPane(DropZone.Left), ["pane.resize.down"] = () => ResizeFocusedPane(DropZone.Below), ["pane.resize.up"] = () => ResizeFocusedPane(DropZone.Above), ["pane.resize.right"] = () => ResizeFocusedPane(DropZone.Right),
            ["pane.zoom"] = ToggleZoom, ["pane.fullscreen"] = TogglePaneFullscreen, ["pane.close"] = () => { if (!CloseFocusedViewport()) HideFocusedRegion(); }, ["pane.split.vertical"] = () => HandleViewportSplitKey(Key.V), ["pane.split.horizontal"] = () => HandleViewportSplitKey(Key.S), ["pane.split.closeView"] = () => HandleViewportSplitKey(Key.Q),
            ["pane.search"] = () => { EnsurePaneVisibleOrSwapTopLeft(PaneKind.Search); FocusPane(PaneKind.Search); },
            ["pane.files"] = () => { EnsurePaneVisibleOrSwapTopLeft(PaneKind.Files); FocusPane(PaneKind.Files); },
            ["problems.next"] = () => CurrentProblems().NextCommand.Execute(null), ["problems.previous"] = () => CurrentProblems().PreviousCommand.Execute(null),
            ["editor.save"] = SaveActiveEditor,
            ["editor.selection.expand"] = ExpandSemanticSelection, ["editor.selection.shrink"] = ShrinkSemanticSelection, ["editor.test.runAtCaret"] = RunTestAtCaret,
            [CSharpEditorCommandCatalog.Rename] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.Rename),
            [CSharpEditorCommandCatalog.ChangeSignature] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ChangeSignature),
            [CSharpEditorCommandCatalog.GoToDefinition] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GoToDefinition),
            [CSharpEditorCommandCatalog.PeekDefinition] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.PeekDefinition),
            [CSharpEditorCommandCatalog.GoToImplementation] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GoToImplementation),
            [CSharpEditorCommandCatalog.GoToTypeDefinition] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GoToTypeDefinition),
            [CSharpEditorCommandCatalog.GoToDeclaration] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GoToDeclaration),
            [CSharpEditorCommandCatalog.FindReferences] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.FindReferences),
            [CSharpEditorCommandCatalog.Format] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.Format),
            [CSharpEditorCommandCatalog.QuickFix] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.QuickFix),
            [CSharpEditorCommandCatalog.OrganizeUsings] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.OrganizeUsings),
            [CSharpEditorCommandCatalog.Cleanup] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.Cleanup),
            [CSharpEditorCommandCatalog.ExtractMethod] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ExtractMethod),
            [CSharpEditorCommandCatalog.ExtractInterface] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ExtractInterface),
            [CSharpEditorCommandCatalog.ExtractClass] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ExtractClass),
            [CSharpEditorCommandCatalog.PullUp] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.PullUp),
            [CSharpEditorCommandCatalog.PushDown] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.PushDown),
            [CSharpEditorCommandCatalog.IntroduceParameter] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.IntroduceParameter),
            [CSharpEditorCommandCatalog.IntroduceVariable] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.IntroduceVariable),
            [CSharpEditorCommandCatalog.IntroduceProperty] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.IntroduceProperty),
            [CSharpEditorCommandCatalog.ExtractConstant] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ExtractConstant),
            [CSharpEditorCommandCatalog.InlineVariable] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.InlineVariable),
            [CSharpEditorCommandCatalog.InlineMethod] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.InlineMethod),
            [CSharpEditorCommandCatalog.SafeDelete] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.SafeDelete),
            [CSharpEditorCommandCatalog.EncapsulateField] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.EncapsulateField),
            [CSharpEditorCommandCatalog.ExtractField] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ExtractField),
            [CSharpEditorCommandCatalog.MoveTypeToFile] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.MoveTypeToFile),
            [CSharpEditorCommandCatalog.GenerateConstructor] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateConstructor),
            [CSharpEditorCommandCatalog.GenerateField] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateField),
            [CSharpEditorCommandCatalog.GenerateProperties] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateProperties),
            [CSharpEditorCommandCatalog.GenerateEquality] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateEquality),
            [CSharpEditorCommandCatalog.GenerateToString] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateToString),
            [CSharpEditorCommandCatalog.GenerateDeconstruct] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateDeconstruct),
            [CSharpEditorCommandCatalog.GenerateMethodFromUsage] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateMethodFromUsage),
            [CSharpEditorCommandCatalog.ImplementInterface] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.ImplementInterface),
            [CSharpEditorCommandCatalog.GenerateOverride] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateOverride),
            [CSharpEditorCommandCatalog.GenerateDelegatingMembers] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateDelegatingMembers),
            [CSharpEditorCommandCatalog.GenerateDisposePattern] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateDisposePattern),
            [CSharpEditorCommandCatalog.GenerateAsyncDisposePattern] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateAsyncDisposePattern),
            [CSharpEditorCommandCatalog.GenerateNullGuards] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateNullGuards),
            [CSharpEditorCommandCatalog.GenerateJsonTypes] = () => ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.GenerateJsonTypes),
            ["stage.cycle"] = () => CycleInActiveMode(1), ["mode.toggle"] = ToggleDisplayMode,
            ["tab.newTerminal"] = () => OnTerminalNewTab(this, new RoutedEventArgs()), ["tab.newEditor"] = () => OnEditorNewTab(this, new RoutedEventArgs()), ["tab.newBrowser"] = () => OnBrowserNewTab(this, new RoutedEventArgs()),
            ["sidebar.explorer"] = () => _vm.ShowExplorerCommand.Execute(null), ["sidebar.tabs"] = () => _vm.ShowTabsCommand.Execute(null), ["sidebar.sessions"] = () => _vm.Sessions.ToggleOpenCommand.Execute(null), ["sidebar.git"] = () => _vm.ShowGitCommand.Execute(null), ["sidebar.pegboard"] = () => _vm.ShowPegboardCommand.Execute(null), ["sidebar.settings"] = () => _vm.ShowSettingsCommand.Execute(null), ["sidebar.appearance"] = () => _vm.ShowAppearanceCommand.Execute(null), ["explorer.revealActiveFile"] = RevealActiveFileInFolderTree, };
        return new KeyboardDispatcher(
            _keybindings,
            actions,
            onEnterMode: mode => { if (mode == CommandCatalog.ResizeMode) SetResizeMode(true); },
            onExitMode: mode => { if (mode == CommandCatalog.ResizeMode) SetResizeMode(false); },
            canExecute: id => !id.StartsWith("editor.csharp.", StringComparison.Ordinal) ||
                ActiveCSharpEditor() is not null);
    }

    /// <summary>現在のエディター文書を保存する。Ctrl+S は言語に依存しないホスト操作なので、
    /// C# 専用 DLL ではなく ShellWindow から Editor の保存 API へ接続する。</summary>
    private void SaveActiveEditor()
    {
        if (_activeEditorTab is not { IsRealized: true } tab)
            return;

        var control = tab.Control;
        if (control.IsVirtualDocument)
        {
            control.ShowStatusMessage("仮想ドキュメントは Ctrl+S では保存できません。");
            return;
        }
        if (control.FilePath is not { Length: > 0 })
            return;

        try
        {
            control.Save();
        }
        catch (Exception ex)
        {
            control.ShowStatusMessage($"保存に失敗しました: {ex.Message}");
        }
    }

    private ProblemsViewModel CurrentProblems()
    {
        var path = _activeEditorTab is { } tab
            ? (tab.IsRealized ? tab.Control.FilePath : tab.PeekFilePath)
            : null;
        var extension = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        return extension is ".ts" or ".tsx" or ".js" or ".jsx"
            ? _vm.TsIde.Problems
            : _vm.Debug.Problems;
    }
}
