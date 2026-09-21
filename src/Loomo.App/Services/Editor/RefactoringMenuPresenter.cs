using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Editor.Controls;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Lsp;
using sk0ya.Loomo.Services.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>リファクタリング候補を遅延取得し、Rename・署名変更・LSP候補のメニューを表示する。</summary>
internal sealed class RefactoringMenuPresenter
{
    private readonly LspManagementService _lspManagement;
    private readonly Func<IReadOnlyList<LspServerRuntimeStatus>> _serverStatuses;
    private readonly Func<string, string> _gestureFor;
    private readonly Action<string, VimEditorControl> _executeCSharpCommand;
    private readonly Action<VimEditorControl> _rename;
    private readonly Func<VimEditorControl, RefactoringItem, Task> _apply;
    private object? _menuToken;

    public RefactoringMenuPresenter(
        LspManagementService lspManagement,
        Func<IReadOnlyList<LspServerRuntimeStatus>> serverStatuses,
        Func<string, string> gestureFor,
        Action<string, VimEditorControl> executeCSharpCommand,
        Action<VimEditorControl> rename,
        Func<VimEditorControl, RefactoringItem, Task> apply)
    {
        _lspManagement = lspManagement;
        _serverStatuses = serverStatuses;
        _gestureFor = gestureFor;
        _executeCSharpCommand = executeCSharpCommand;
        _rename = rename;
        _apply = apply;
    }

    public MenuItem? BuildMenuItem(VimEditorControl? control)
    {
        if (RefactorDebugLog.IsEnabled)
            RefactorDebugLog.Write(
                $"menu: file={control?.FilePath ?? "(null)"} " +
                $"lspDoc={(control?.LspDocument is null ? "null" : $"connected={control.LspDocument.IsConnected} ready={control.LspDocument.IsReady}")} " +
                $"hasSelection={control?.HasSelection} range={Describe(control?.SelectionAsLspRange())}");

        if (control?.FilePath is not { Length: > 0 } filePath)
            return null;
        if (!CSharpSignatureRefactoring.AppliesTo(filePath) &&
            control.LspDocument is not { IsConnected: true })
            return null;

        var root = new MenuItem { Header = "リファクタリング" };
        AutomationProperties.SetAutomationId(root, "CSharpRefactoring");
        AutomationProperties.SetName(root, root.Header.ToString());
        root.Items.Add(new MenuItem { Header = "候補を取得しています…", IsEnabled = false });
        root.SubmenuOpened += (_, _) => _ = PopulateAsync(root, control);
        return root;
    }

    private async Task PopulateAsync(MenuItem root, VimEditorControl control)
    {
        var token = new object();
        _menuToken = token;

        // Selection/Caret は UI スレッド上で読み取り、以降は要求サービスが解析とLSP要求を並行する。
        var filePath = control.FilePath;
        var caret = control.Caret;
        var text = control.Text;
        var request = await RefactoringRequestController.LoadAsync(
            filePath, text, caret.Line, caret.Column,
            control.SelectionAsLspRange(), control.LspDocument);
        if (!ReferenceEquals(_menuToken, token))
            return;

        root.Items.Clear();
        root.Items.Add(BuildRenameMenuItem(control));
        if (request.Signature is { } signature)
            root.Items.Add(BuildChangeSignatureMenuItem(signature, control));

        var groups = RefactoringMenu.Build(request.Actions);
        if (RefactorDebugLog.IsEnabled)
            RefactorDebugLog.Write(
                $"populate: signature={(request.Signature is null ? "-" : request.Signature.Name)} actions={request.Actions.Count} " +
                $"[{string.Join(" | ", request.Actions.Select(action => $"{action.Kind ?? "-"}:{action.Title}"))}] " +
                $"menuItems={groups.Sum(group => group.Items.Count)}");

        foreach (var (_, _, items) in groups)
        {
            root.Items.Add(new Separator());
            foreach (var refactoring in items)
            {
                var item = new MenuItem
                {
                    Header = MenuHeaderText.Escape(refactoring.Title),
                    ToolTip = refactoring.ServerTitle,
                };
                AutomationProperties.SetAutomationId(item, $"CSharpRefactorAction.{root.Items.Count}");
                AutomationProperties.SetName(item, item.Header.ToString());
                item.Click += (_, _) => _ = _apply(control, refactoring);
                root.Items.Add(item);
            }
        }

        if (groups.Count == 0)
        {
            root.Items.Add(new Separator());
            root.Items.Add(new MenuItem
            {
                Header = RefactoringRequestController.NoActionsMessage(
                    filePath, _lspManagement, _serverStatuses()),
                IsEnabled = false,
            });
        }
    }

    private MenuItem BuildRenameMenuItem(VimEditorControl control)
    {
        var item = new MenuItem
        {
            Header = CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.Rename),
            InputGestureText = _gestureFor(CSharpEditorCommandCatalog.Rename) is { Length: > 0 } key
                ? key
                : "LSP",
            Tag = CSharpEditorCommandCatalog.Rename,
        };
        AutomationProperties.SetAutomationId(item, CSharpEditorCommandCatalog.Rename);
        AutomationProperties.SetName(item, item.Header.ToString());
        item.Click += (_, _) => _rename(control);
        return item;
    }

    private MenuItem BuildChangeSignatureMenuItem(MethodSignature signature, VimEditorControl control)
    {
        var item = new MenuItem
        {
            Header = CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.ChangeSignature),
            InputGestureText = _gestureFor(CSharpEditorCommandCatalog.ChangeSignature),
            ToolTip = signature.Display,
            Tag = CSharpEditorCommandCatalog.ChangeSignature,
        };
        AutomationProperties.SetAutomationId(item, CSharpEditorCommandCatalog.ChangeSignature);
        AutomationProperties.SetName(item, item.Header.ToString());
        item.Click += (_, _) => _executeCSharpCommand(CSharpEditorCommandCatalog.ChangeSignature, control);
        return item;
    }

    private static string Describe(LspRange? range) => range is { } value
        ? $"{value.Start.Line},{value.Start.Character}-{value.End.Line},{value.End.Character}"
        : "(none)";
}
