using System.Diagnostics;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタ右クリックの「Quick Fix」。
///
/// <para>ライブラリのネイティブ項目（「この位置で使える修正」）と置き換わる項目
/// （<c>ShellWindow.EditorNativeMenu</c>）。ネイティブ側は候補を<b>キャンバスに描くポップアップ</b>で
/// 見せるので、j/k と Enter でしか選べない——右クリックから入ったのにマウスでは 1 件も適用できず、
/// 「押しても何も起きない」ように見えていた。ここではリファクタリング（設計書 §32）と同じく
/// <b>サブメニューに候補を並べ、クリックで適用する</b>。</para>
///
/// <para>候補は診断を返した言語サーバーへ <c>only: ["quickfix"]</c> で先に問い合わせ、
/// 無い場合だけホスト（Roslyn／StyleCop の <see cref="CSharpEditorQuickFixCoordinator"/>）へフォールバックする。
/// 適用は
/// <see cref="ApplyLspWorkspaceEdit"/> に集約されるので、編集プレビューも取り消しも同じ道を通る。</para></summary>
public partial class ShellWindow
{
    /// <summary>開き直しの競合で古い応答が新しいメニューを上書きしないようにする番兵。</summary>
    private object? _quickFixMenuToken;

    private QuickFixApplicationCoordinator? _quickFixApplication;
    private QuickFixApplicationCoordinator QuickFixApplication => _quickFixApplication ??= new(
        ShowRefactorStatus,
        edit => ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
            expectedTexts: edit.ExpectedTexts));

    /// <summary>「Quick Fix」項目。中身は<b>開いたときに</b>詰める——右クリックしただけで
    /// Roslyn や言語サーバーへ問い合わせが飛ばないようにする（§32 のリファクタリングと同じ作法）。</summary>
    private MenuItem BuildQuickFixMenuItem(VimEditorControl control)
    {
        var root = new MenuItem
        {
            Header = "Quick Fix",
            // キー表記は .cs のときだけ出す。Alt+Enter は C# 専用の結線なので、
            // 他言語で見せると押せないキーを案内することになる。
            InputGestureText = ActiveCSharpEditor(control) is not null
                ? GestureFor(CSharpEditorCommandCatalog.QuickFix)
                : "",
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(root, "EditorQuickFix");
        System.Windows.Automation.AutomationProperties.SetName(root, "Quick Fix");
        root.Items.Add(new MenuItem { Header = "候補を取得しています…", IsEnabled = false });
        root.SubmenuOpened += (_, _) => _ = PopulateQuickFixMenuAsync(root, control);
        return root;
    }

    /// <summary>サブメニューが開かれた時点で候補を取り直す（キャレットや選択が動けば候補も変わる）。</summary>
    private async Task PopulateQuickFixMenuAsync(MenuItem root, VimEditorControl control)
    {
        var token = new object();
        _quickFixMenuToken = token;

        var filePath = control.FilePath;
        var range = control.SelectionAsLspRange() ?? new LspRange(
            new LspPosition(control.Caret.Line, control.Caret.Column),
            new LspPosition(control.Caret.Line, control.Caret.Column));
        var clock = Stopwatch.StartNew();
        var actions = await RequestQuickFixesAsync(control, range);
        var actionText = control.Text;
        RefactorDebugLog.Write(
            $"quickfix menu populated file={filePath ?? "(unknown)"} elapsed={clock.ElapsedMilliseconds}ms actions={actions.Count}");
        if (!ReferenceEquals(_quickFixMenuToken, token)) return;

        root.Items.Clear();
        if (actions.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = DescribeNoQuickFixes(filePath), IsEnabled = false });
            return;
        }

        if (QuickFixActionSnapshotResolver.Resolve(
                control, filePath, actionText, GetDiagnosticSession) is not { } snapshot)
            return;

        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                Header = MenuHeaderText.Escape(action.Title),
                ToolTip = action.DisabledReason ?? action.Title,
                IsEnabled = action.DisabledReason is null,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                item, $"EditorQuickFixAction.{root.Items.Count}");
            System.Windows.Automation.AutomationProperties.SetName(item, action.Title);
            var captured = action;
            item.Click += (_, _) => _ = ApplyQuickFixAsync(
                control, captured, snapshot.DiagnosticSession, snapshot.DocumentVersion,
                snapshot.DiagnosticSnapshotId, filePath ?? "", actionText, snapshot.LanguageServerVersion);
            root.Items.Add(item);
        }
    }

    /// <summary>Alt+Enter と同じ診断スナップショットを使い、文書版が一致する候補だけ返す。</summary>
    private Task<IReadOnlyList<LspCodeAction>> RequestQuickFixesAsync(
        VimEditorControl control, LspRange range)
        => QuickFixRequestController.RequestAsync(
            control, range, RefactoringRequestController.RequestTimeout,
            EnsureCSharpDiagnosticAnalysisScheduled,
            QuickFixCoordinator.RequestAsync,
            RefactorDebugLog.Write);

    /// <summary>候補 0 件の理由を、分かる範囲で言い分ける。C# の修正は<b>ソリューションの読み込み</b>が
    /// 終わるまで必ず 0 件になるので、そこで「ありません」と言い切ると嘘になる。</summary>
    private string DescribeNoQuickFixes(string? filePath)
        => QuickFixEmptyStatePresenter.Describe(
            filePath, _editorTabs, _diagnosticSessions, _solutionModel?.Current,
            _lspManagement, _lspWorkspace.ServerStatuses);

    /// <summary>選ばれた修正を適用する。未解決なら <c>codeAction/resolve</c>、command 型なら
    /// <c>workspace/executeCommand</c>——どちらも編集は <see cref="ApplyLspWorkspaceEdit"/> へ落ちる。</summary>
    private Task ApplyQuickFixAsync(
        VimEditorControl control,
        LspCodeAction action,
        EditorDiagnosticSession? diagnosticSession,
        int documentVersion,
        long diagnosticSnapshotId,
        string documentPath,
        string documentText,
        int? languageServerVersion)
        => QuickFixApplication.ApplyAsync(
            control, action, diagnosticSession, documentVersion, diagnosticSnapshotId,
            documentPath, documentText, languageServerVersion);
}
