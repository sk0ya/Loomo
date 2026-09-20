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
/// 無い場合だけホスト（Roslyn／StyleCop の <see cref="RequestCSharpQuickFixesAsync"/>）へフォールバックする。
/// 適用は
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

        EditorDiagnosticSession? diagnosticSession = null;
        var actionVersion = 0;
        long actionSnapshotId = 0;
        int? languageServerVersion = null;
        if (filePath is { Length: > 0 } documentPath &&
            string.Equals(Path.GetExtension(documentPath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            diagnosticSession = GetDiagnosticSession(control);
            if (!diagnosticSession.TryGetCurrent(documentPath, actionText, out var actionSnapshot))
                return;
            actionVersion = actionSnapshot.Version;
            actionSnapshotId = actionSnapshot.SnapshotId;
            languageServerVersion = actionSnapshot.LanguageServerVersion;
        }
        else if (control.LspDocument is { IsReady: true, IsConnected: true } actionDocument &&
                 string.Equals(actionDocument.Text, actionText, StringComparison.Ordinal))
        {
            // C#以外は中央C#診断セッションを通さず、従来どおり各LSPの候補を使う。
            languageServerVersion = actionDocument.Version;
        }
        else
        {
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
            item.Click += (_, _) => _ = ApplyQuickFixAsync(
                control, captured, diagnosticSession, actionVersion,
                actionSnapshotId, filePath ?? "", actionText, languageServerVersion);
            root.Items.Add(item);
        }
    }

    /// <summary>Alt+Enter と同じ診断スナップショットを使い、文書版が一致する候補だけ返す。</summary>
    private async Task<IReadOnlyList<LspCodeAction>> RequestQuickFixesAsync(
        VimEditorControl control, LspRange range)
    {
        var totalClock = Stopwatch.StartNew();
        var filePath = control.FilePath ?? "(unknown)";
        var source = control.Text;
        var isCSharp = string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);
        var session = isCSharp && filePath != "(unknown)"
            ? EnsureCSharpDiagnosticAnalysisScheduled(control, filePath, source)
            : null;
        var version = session?.Version ?? 0;
        RefactorDebugLog.Write(
            $"quickfix start file={filePath} range={range.Start.Line}:{range.Start.Character}-{range.End.Line}:{range.End.Character}");
        IReadOnlyList<LspCodeAction> actions = [];
        if (!isCSharp)
        {
            if (control.LspDocument is not { IsReady: true, IsConnected: true } lspDocument ||
                !string.Equals(lspDocument.Text, source, StringComparison.Ordinal))
                return [];
            var currentLspVersion = lspDocument.Version;
            using var cts = new CancellationTokenSource(RefactorRequestTimeout);
            var lspClock = Stopwatch.StartNew();
            try
            {
                actions = await lspDocument.RequestCodeActionsAsync(
                    range, [LspCodeActionKinds.QuickFix], cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RefactorDebugLog.Write($"quickfix lsp error file={filePath} message={ex.Message}"); }
            finally
            {
                RefactorDebugLog.Write(
                    $"quickfix lsp file={filePath} elapsed={lspClock.ElapsedMilliseconds}ms actions={actions.Count}");
            }
            if (!string.Equals(control.Text, source, StringComparison.Ordinal) ||
                lspDocument.Version != currentLspVersion || !ReferenceEquals(control.LspDocument, lspDocument))
                return [];
            return actions;
        }

        var snapshot = session is not null && session.TryGetCurrent(filePath, source, out var current)
            ? current
            : null;
        if (snapshot is not null &&
            control.LspDocument is { IsReady: true, IsConnected: true } document &&
            document.Version is { } documentVersion &&
            snapshot.LanguageServerVersion == documentVersion &&
            string.Equals(document.Text, source, StringComparison.Ordinal))
        {
            // 表示に使っている診断を返した文書版へだけ要求する。版の無い／古いLSP診断は使わない。
            using var cts = new CancellationTokenSource(RefactorRequestTimeout);
            var lspClock = Stopwatch.StartNew();
            try
            {
                actions = await document.RequestCodeActionsAsync(
                    range, [LspCodeActionKinds.QuickFix], cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RefactorDebugLog.Write($"quickfix lsp error file={filePath} message={ex.Message}"); }
            finally
            {
                RefactorDebugLog.Write(
                    $"quickfix lsp file={filePath} elapsed={lspClock.ElapsedMilliseconds}ms actions={actions.Count}");
            }
            if (session!.Version != version || session.SnapshotId != snapshot.SnapshotId ||
                !string.Equals(control.Text, source, StringComparison.Ordinal) ||
                document.Version != documentVersion)
                return [];
            if (actions.Count > 0)
            {
                RefactorDebugLog.Write($"quickfix done file={filePath} source=lsp total={totalClock.ElapsedMilliseconds}ms");
                return actions;
            }
        }

        // LSPが候補を返さない場合だけ、StyleCop／ローカルRoslynをフォールバックとして使う。
        var hostClock = Stopwatch.StartNew();
        if (session is null) return [];
        try
        {
            actions = await RequestCSharpQuickFixesAsync(
                control, range, [LspCodeActionKinds.QuickFix]);
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex) { RefactorDebugLog.Write($"quickfix host error file={filePath} message={ex.Message}"); }
        finally
        {
            RefactorDebugLog.Write(
                $"quickfix host file={filePath} elapsed={hostClock.ElapsedMilliseconds}ms actions={actions.Count}");
        }
        if (session.Version != version || !string.Equals(control.Text, source, StringComparison.Ordinal))
            return [];
        RefactorDebugLog.Write(
            $"quickfix done file={filePath} source=host total={totalClock.ElapsedMilliseconds}ms actions={actions.Count}");
        return actions;
    }

    /// <summary>候補 0 件の理由を、分かる範囲で言い分ける。C# の修正は<b>ソリューションの読み込み</b>が
    /// 終わるまで必ず 0 件になるので、そこで「ありません」と言い切ると嘘になる。</summary>
    private string DescribeNoQuickFixes(string? filePath)
    {
        if (filePath is { Length: > 0 } currentPath &&
            string.Equals(Path.GetExtension(currentPath), ".cs", StringComparison.OrdinalIgnoreCase) &&
            _editorTabs.FirstOrDefault(tab => tab.IsRealized && tab.Control.FilePath is { Length: > 0 } editorPath &&
                string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
                is { } tab &&
            (!_diagnosticSessions.TryGetValue(tab.Control, out var session) ||
             !session.TryGetCurrent(currentPath, tab.Control.Text, out _)))
            return "診断を更新しています…";

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
    private async Task ApplyQuickFixAsync(
        VimEditorControl control,
        LspCodeAction action,
        EditorDiagnosticSession? diagnosticSession,
        int documentVersion,
        long diagnosticSnapshotId,
        string documentPath,
        string documentText,
        int? languageServerVersion)
    {
        try
        {
            if (!IsQuickFixSnapshotCurrent(control, diagnosticSession, documentVersion,
                    diagnosticSnapshotId, documentPath, documentText, languageServerVersion))
            {
                ShowRefactorStatus("文書が変更されたため、Quick Fixを取り直してください。");
                return;
            }

            if (action.Edit is null && action.NeedsResolve &&
                control.LspDocument is { IsConnected: true } resolveDocument)
                action = await resolveDocument.ResolveCodeActionAsync(action) ?? action;

            if (!IsQuickFixSnapshotCurrent(control, diagnosticSession, documentVersion,
                    diagnosticSnapshotId, documentPath, documentText, languageServerVersion))
            {
                ShowRefactorStatus("文書が変更されたため、Quick Fixを取り直してください。");
                return;
            }

            if (action.Edit is { } edit && (edit.Changes.Count > 0 || edit.FileOperations is { Count: > 0 }))
            {
                var outcome = ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
                    expectedTexts: edit.ExpectedTexts);
                ShowRefactorStatus(outcome.Describe(action.Title) ?? $"「{action.Title}」を適用しました。");
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

    private static bool IsQuickFixSnapshotCurrent(
        VimEditorControl control,
        EditorDiagnosticSession? diagnosticSession,
        int documentVersion,
        long diagnosticSnapshotId,
        string documentPath,
        string documentText,
        int? languageServerVersion)
    {
        if (!string.Equals(Path.GetFullPath(control.FilePath ?? ""), Path.GetFullPath(documentPath),
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(control.Text, documentText, StringComparison.Ordinal)) return false;
        if (diagnosticSession is not null &&
            (diagnosticSession.Version != documentVersion || diagnosticSession.SnapshotId != diagnosticSnapshotId))
            return false;
        return !languageServerVersion.HasValue || control.LspDocument?.Version == languageServerVersion;
    }
}
