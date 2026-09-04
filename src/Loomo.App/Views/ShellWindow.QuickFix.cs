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
/// <para>候補の出どころも適用も Alt+Enter と同じにする——ホスト（Roslyn／StyleCop の
/// <see cref="RequestCSharpQuickFixesAsync"/>）を先に見て、無ければ言語サーバーへ
/// <c>only: ["quickfix"]</c> で問い合わせる。適用は
/// <see cref="ApplyLspWorkspaceEdit"/> に集約されるので、編集プレビューも取り消しも同じ道を通る。</para></summary>
public partial class ShellWindow
{
    /// <summary>開き直しの競合で古い応答が新しいメニューを上書きしないようにする番兵。</summary>
    private object? _quickFixMenuToken;

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
        var range = control.SelectionAsLspRange() ?? CaretRange(control);
        var actions = await RequestQuickFixesAsync(control, range);
        if (!ReferenceEquals(_quickFixMenuToken, token)) return;

        root.Items.Clear();
        if (actions.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = DescribeNoQuickFixes(filePath), IsEnabled = false });
            return;
        }

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
            item.Click += (_, _) => _ = ApplyQuickFixAsync(control, captured);
            root.Items.Add(item);
        }
    }

    /// <summary>Alt+Enter と同じ順序で候補を集める。ホスト（Roslyn／StyleCop）が先で、
    /// 出なければ言語サーバーへ <c>quickfix</c> だけを問い合わせる。</summary>
    private async Task<IReadOnlyList<LspCodeAction>> RequestQuickFixesAsync(
        VimEditorControl control, LspRange range)
    {
        IReadOnlyList<LspCodeAction> actions = [];
        try { actions = await RequestCSharpQuickFixesAsync(control, range, [LspCodeActionKinds.QuickFix]); }
        catch (OperationCanceledException) { return []; }
        catch { actions = []; }
        if (actions.Count > 0) return actions;

        if (control.LspDocument is not { IsConnected: true } document) return [];
        using var cts = new CancellationTokenSource(RefactorRequestTimeout);
        try { return await document.RequestCodeActionsAsync(range, [LspCodeActionKinds.QuickFix], cts.Token); }
        catch (OperationCanceledException) { return []; }
        catch { return []; }
    }

    /// <summary>候補 0 件の理由を、分かる範囲で言い分ける。C# の修正は<b>ソリューションの読み込み</b>が
    /// 終わるまで必ず 0 件になるので、そこで「ありません」と言い切ると嘘になる。</summary>
    private string DescribeNoQuickFixes(string? filePath)
    {
        if (filePath is { Length: > 0 } path &&
            string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase) &&
            _solutionModel?.Current.ProjectForFile(path) is not { State: ProjectLoadState.Ready })
            return "ソリューションを読み込んでいます（読み込みが終わると使えます）";
        if (IsLanguageServerReadyFor(filePath) is false)
            return "言語サーバーの準備中です（プロジェクトの読み込みが終わると使えます）";
        return "この位置に適用できる修正はありません";
    }

    /// <summary>選ばれた修正を適用する。未解決なら <c>codeAction/resolve</c>、command 型なら
    /// <c>workspace/executeCommand</c>——どちらも編集は <see cref="ApplyLspWorkspaceEdit"/> へ落ちる。</summary>
    private async Task ApplyQuickFixAsync(VimEditorControl control, LspCodeAction action)
    {
        try
        {
            if (action.Edit is null && action.NeedsResolve &&
                control.LspDocument is { IsConnected: true } resolveDocument)
                action = await resolveDocument.ResolveCodeActionAsync(action) ?? action;

            if (action.Edit is { } edit && (edit.Changes.Count > 0 || edit.FileOperations is { Count: > 0 }))
            {
                var error = ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
#if LOOMO_EDITOR_HOST_API
                    expectedTexts: edit.ExpectedTexts);
#else
                    expectedTexts: null);
#endif
                ShowRefactorStatus(error is null
                    ? $"「{action.Title}」を適用しました。"
                    : $"「{action.Title}」を適用できませんでした: {error}");
                return;
            }

            if (action.Command is { } command)
            {
                if (control.LspDocument is not { IsConnected: true } commandDocument)
                {
                    ShowRefactorStatus($"「{action.Title}」: 言語サーバーに接続していません。");
                    return;
                }
                // 編集は応答ではなくサーバー起点の applyEdit で返る（OnLspServerApplyEditRequested）。
                if (!await commandDocument.ExecuteCommandAsync(command))
                    ShowRefactorStatus($"「{action.Title}」: サーバーがコマンドを実行できませんでした。");
                return;
            }

            ShowRefactorStatus($"「{action.Title}」: 適用できる編集が返りませんでした。");
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"「{action.Title}」を適用できませんでした: {ex.Message}");
        }
    }
}
