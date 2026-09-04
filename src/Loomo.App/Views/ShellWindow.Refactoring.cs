using sk0ya.Loomo.Services.Lsp;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Refactoring;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタの右クリック「リファクタリング」（設計書 §32）。
///
/// <para>候補は言語サーバーの <c>textDocument/codeAction</c>（<c>only: ["refactor"]</c>）から取る。
/// **選択があれば範囲で、無ければキャレット位置で**問い合わせる——「メソッドの抽出」のように
/// 範囲そのものが対象のリファクタリングは、1点の要求では候補に出ない。</para>
///
/// <para>メニューは同期的に組み立てる必要がある一方、候補の取得も「シグネチャの変更が使えるか」の
/// 判定（＝構文解析）も即答できないので、**サブメニューを開いた時点で中身を作る**
/// （<see cref="MenuItem.SubmenuOpened"/>）。開くまで LSP を叩かないので、右クリックしただけで
/// 言語サーバーへ問い合わせが飛ぶこともない。**使えない項目は出さない**——押しても
/// 「できません」としか返らない項目を並べない。</para>
///
/// <para>適用は3経路ある。(1) edit を持つアクションはそのまま適用、(2) <c>data</c> だけの未解決
/// アクションは <c>codeAction/resolve</c> してから適用（Roslyn はこちら）、(3) command 型は
/// <c>workspace/executeCommand</c> で実行し、編集はサーバー起点の <c>workspace/applyEdit</c> で
/// 返ってくる（tsserver 系がこちら）。</para></summary>
public partial class ShellWindow
{
    /// <summary>リファクタリング候補の取得を諦めるまでの時間。プロジェクト解析中の Roslyn は
    /// 数秒返さないことがあるが、メニューを開いたまま無限に待たせない。</summary>
    private static readonly TimeSpan RefactorRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>開き直しの競合で古い応答が新しいメニューを上書きしないようにする番兵。</summary>
    private object? _refactorMenuToken;

    private void InitializeRefactoringWiring()
        => _lspWorkspace.ApplyEditRequested += OnLspServerApplyEditRequested;

    /// <summary>右クリックメニューへ「リファクタリング」を足す。中身は開いたときに詰める。
    ///
    /// <para>C#はLSP未接続でも専用DLLのrename／Change Signatureを使えるため、C#ファイルに限り
    /// 接続条件を外す。他言語は従来どおり、コントロール側が "Rename Symbol" を出す条件（接続済み）に
    /// 揃える。ここを `IsReady`（didOpen 済み）まで絞ると、接続直後のわずかな間だけ
    /// **どちらのメニューにも名前の変更が無い**状態が生まれる——ネイティブ項目は
    /// <c>HostProvidesRenameMenuItem</c> で消してあるため。</para></summary>
    private void AddRefactorMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (RefactorDebugLog.IsEnabled)
            RefactorDebugLog.Write(
                $"menu: file={control?.FilePath ?? "(null)"} " +
                $"lspDoc={(control?.LspDocument is null ? "null" : $"connected={control.LspDocument.IsConnected} ready={control.LspDocument.IsReady}")} " +
                $"hasSelection={control?.HasSelection} range={Describe(control?.SelectionAsLspRange())}");

        if (control?.FilePath is not { Length: > 0 } filePath) return;
        var isCSharp = string.Equals(Path.GetExtension(filePath), ".cs",
            StringComparison.OrdinalIgnoreCase);
        if (!isCSharp && control.LspDocument is not { IsConnected: true }) return;

        var root = new MenuItem { Header = "リファクタリング" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(root, "CSharpRefactoring");
        System.Windows.Automation.AutomationProperties.SetName(root, root.Header.ToString());
        root.Items.Add(new MenuItem { Header = "候補を取得しています…", IsEnabled = false });
        root.SubmenuOpened += (_, _) => _ = PopulateRefactorMenuAsync(root, control);
        menu.Items.Add(root);
    }

    /// <summary>サブメニューが開かれた時点で中身を作る。2回目以降も作り直す——
    /// 選択やキャレットが動けば、使える操作も候補も変わるため。</summary>
    private async Task PopulateRefactorMenuAsync(MenuItem root, VimEditorControl control)
    {
        var token = new object();
        _refactorMenuToken = token;

        // 解析に渡す材料は UI スレッドで確定させてから、重い処理を待つ。
        var filePath = control.FilePath;
        var caret = control.Caret;
        var text = control.Text;

        // 構文解析と LSP 要求は独立なので同時に走らせる（LSP 側が数秒かかることがあるため）。
        var signatureTask = FindChangeableSignatureAsync(filePath, text, caret.Line, caret.Column);
        var actionsTask = RequestRefactoringsAsync(control);
        var signature = await signatureTask;
        var actions = await actionsTask;
        if (!ReferenceEquals(_refactorMenuToken, token)) return;

        root.Items.Clear();
        root.Items.Add(BuildRenameMenuItem(control));
        if (signature is not null)
        {
            var item = new MenuItem
            {
                // 見出しとキー表記はカタログから引く（コマンドパレットと同じ綴りにする）。
                Header = CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.ChangeSignature),
                InputGestureText = GestureFor(CSharpEditorCommandCatalog.ChangeSignature),
                ToolTip = signature.Display,
                Tag = CSharpEditorCommandCatalog.ChangeSignature,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                item, CSharpEditorCommandCatalog.ChangeSignature);
            System.Windows.Automation.AutomationProperties.SetName(item, item.Header.ToString());
            item.Click += (_, _) => ExecuteCSharpEditorCommand(
                CSharpEditorCommandCatalog.ChangeSignature, control);
            root.Items.Add(item);
        }

        var groups = RefactoringMenu.Build(actions);
        if (RefactorDebugLog.IsEnabled)
            RefactorDebugLog.Write(
                $"populate: signature={(signature is null ? "-" : signature.Name)} actions={actions.Count} " +
                $"[{string.Join(" | ", actions.Select(a => $"{a.Kind ?? "-"}:{a.Title}"))}] " +
                $"menuItems={groups.Sum(g => g.Items.Count)}");

        foreach (var (_, _, items) in groups)
        {
            root.Items.Add(new Separator());
            foreach (var item in items)
            {
                var menuItem = new MenuItem
                {
                    Header = MenuHeaderText.Escape(item.Title),
                    ToolTip = item.ServerTitle,
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(
                    menuItem, $"CSharpRefactorAction.{root.Items.Count}");
                System.Windows.Automation.AutomationProperties.SetName(menuItem, menuItem.Header.ToString());
                var captured = item;
                menuItem.Click += (_, _) => _ = ApplyRefactoringAsync(control, captured);
                root.Items.Add(menuItem);
            }
        }

        if (groups.Count == 0)
        {
            root.Items.Add(new Separator());
            root.Items.Add(new MenuItem { Header = DescribeNoRefactorings(filePath), IsEnabled = false });
        }
    }

    /// <summary>候補0件の理由を、分かる範囲で言い分ける。
    /// **プロジェクト読込中の Roslyn は 0 件を即答する**ので、「ありません」と出すと
    /// 「本当に無い」のか「まだ待てば出る」のか区別がつかない（実測: 解析完了前は必ず 0 件、
    /// 完了後は同じ範囲で「メソッドを抽出する」が返る）。</summary>
    private string DescribeNoRefactorings(string? filePath)
    {
        if (IsLanguageServerReadyFor(filePath) is false)
            return "言語サーバーの準備中です（プロジェクトの読み込みが終わると使えます）";
        // 「無い」と言い切らない。Roslyn は**プロジェクト解析が終わるまで空配列を即答する**うえ、
        // 診断だけは先に届くので Loomo 側の ready 判定はもう ready になっている。大きな
        // ソリューションでは数分かかることがあり、そこで「ありません」と断言すると嘘になる。
        return "候補がありません（言語サーバーの解析が終わっていない可能性があります）";
    }

    /// <summary>このファイルを担当する言語サーバーが ready か。判らなければ null。</summary>
    private bool? IsLanguageServerReadyFor(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = LspExtensions.NormalizeExt(Path.GetExtension(filePath));
        if (ext.Length == 0 || _lspManagement.ResolveServerFor(ext) is not { } server) return null;

        var statuses = _lspWorkspace.ServerStatuses
            .Where(s => string.Equals(
                Path.GetFileNameWithoutExtension(s.Executable),
                Path.GetFileNameWithoutExtension(server.Executable),
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (statuses.Count == 0) return null;
        return statuses.Any(s => s.State == LspServerRuntimeState.Ready);
    }

    /// <summary>「名前の変更」は<b>C#専用ではない</b>——このメニュー自体、LSP接続済みなら他言語でも出る。
    /// C#は他の入口（コマンドパレット／キーバインド）と同じホスト結線を通し、それ以外は
    /// コントロール自身のLSP renameを直接呼ぶ。<see cref="ExecuteCSharpEditorCommand"/> は
    /// 拡張子が .cs でなければ黙って返るので、ここで分けないと他言語で項目が無反応になる。</summary>
    private MenuItem BuildRenameMenuItem(VimEditorControl control)
    {
        var item = new MenuItem
        {
            Header = CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.Rename),
            // 実効キー（未割当なら LSP 由来であることだけ示す）。
            InputGestureText = GestureFor(CSharpEditorCommandCatalog.Rename) is { Length: > 0 } key
                ? key : "LSP",
            Tag = CSharpEditorCommandCatalog.Rename,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            item, CSharpEditorCommandCatalog.Rename);
        System.Windows.Automation.AutomationProperties.SetName(item, item.Header.ToString());
        item.Click += (_, _) =>
        {
            if (ActiveCSharpEditor(control) is not null)
                ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.Rename, control);
            else
                control.ExecuteCommand("Rename");
        };
        return item;
    }

    /// <summary>「シグネチャの変更」が実際に使える位置か。使えるなら読み取った宣言、駄目なら null。
    /// 構文解析はバックグラウンドで行う（大きなファイルでメニューが引っかからないように）。</summary>
    private async Task<MethodSignature?> FindChangeableSignatureAsync(
        string? filePath, string text, int line, int column)
    {
        if (!CSharpSignatureRefactoring.AppliesTo(filePath)) return null;
        try
        {
            var target = await Task.Run(() => CSharpSignatureSyntax.Read(
                filePath!, Editor.Core.Lsp.LspUri.FromPath(Path.GetFullPath(filePath!)),
                text, line, column));
            return target.Signature;
        }
        catch { return null; }
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestRefactoringsAsync(VimEditorControl control)
    {
        if (control.LspDocument is not { IsConnected: true } document) return [];
        var range = control.SelectionAsLspRange() ?? CaretRange(control);
        using var cts = new CancellationTokenSource(RefactorRequestTimeout);
        try { return await document.RequestCodeActionsAsync(range, RefactoringMenu.RequestKinds, cts.Token); }
        catch (OperationCanceledException) { return []; }
        catch { return []; }
    }

    private static string Describe(LspRange? range) => range is { } r
        ? $"{r.Start.Line},{r.Start.Character}-{r.End.Line},{r.End.Character}"
        : "(none)";

    private static LspRange CaretRange(VimEditorControl control)
    {
        var position = new LspPosition(control.Caret.Line, control.Caret.Column);
        return new LspRange(position, position);
    }

    /// <summary>選ばれたリファクタリングを適用する。未解決なら解決し、コマンド型なら実行する。</summary>
    private async Task ApplyRefactoringAsync(VimEditorControl control, RefactoringItem item)
    {
        if (control.LspDocument is not { IsConnected: true } document)
        {
            ShowRefactorStatus($"「{item.Title}」: 言語サーバーに接続していません。");
            return;
        }

        var action = item.Action;
        try
        {
            if (action.Edit is null && action.NeedsResolve)
                action = await document.ResolveCodeActionAsync(action) ?? action;

            if (action.Edit is { } edit && (edit.Changes.Count > 0 || edit.FileOperations is { Count: > 0 }))
            {
                // 抽出は「切り出して名前を付ける」操作。NewMethod のまま置いていくのは操作の半分。
                var changes = RenameExtractedSymbol(item, edit.Changes, out bool cancelled);
                if (cancelled) return;

                var error = ApplyLspWorkspaceEdit(changes, edit.DocumentVersions, edit.FileOperations,
#if LOOMO_EDITOR_HOST_API
                    expectedTexts: edit.ExpectedTexts);
#else
                    expectedTexts: null);
#endif
                ShowRefactorStatus(error is null
                    ? $"「{item.Title}」を適用しました。"
                    : $"「{item.Title}」を適用できませんでした: {error}");
                return;
            }

            if (action.Command is { } command)
            {
                // 編集は応答ではなくサーバー起点の applyEdit で返る（OnLspServerApplyEditRequested）。
                bool sent = await document.ExecuteCommandAsync(command);
                if (!sent) ShowRefactorStatus($"「{item.Title}」: サーバーがコマンドを実行できませんでした。");
                return;
            }

            ShowRefactorStatus($"「{item.Title}」: 適用できる編集がサーバーから返りませんでした。");
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"「{item.Title}」を適用できませんでした: {ex.Message}");
        }
    }

    /// <summary>抽出系リファクタリングなら、適用前に新しいメソッド名を訊いて差し替える
    /// （Rider / VS と同じく**名前を先に決める**）。抽出でない・名前を取り出せない言語なら素通し。
    /// キャンセルされたら <paramref name="cancelled"/> を立てて**何も適用しない**。</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> RenameExtractedSymbol(
        RefactoringItem item,
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes,
        out bool cancelled)
    {
        cancelled = false;
        if (item.Group != RefactoringGroup.Extract) return changes;
        if (ExtractedSymbolName.Find(changes) is not { Length: > 0 } generated) return changes;

        while (true)
        {
            var chosen = InputDialog.Prompt(
                this, "リファクタリング", $"「{item.Title}」— 新しい名前", generated);
            if (chosen is null) { cancelled = true; return changes; }
            if (ExtractedSymbolName.IsValidIdentifier(chosen))
                return ExtractedSymbolName.Rename(changes, generated, chosen);

            MessageBox.Show(this, "識別子として使えない名前です。", "リファクタリング",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>サーバー起点の <c>workspace/applyEdit</c>。<b>LSP の読み取りスレッドで発火し、
    /// ここが戻るまでサーバーは応答を待って止まっている</b>ので、UI スレッドへは同期的に入る
    /// （呼び出し元の UI は LSP 呼び出しを await 中＝ブロックしていないので、これで詰まらない）。</summary>
    private void OnLspServerApplyEditRequested(object? sender, LspApplyEditEventArgs e)
    {
        var error = Dispatcher.Invoke(() =>
            ApplyLspWorkspaceEdit(e.Edit.Changes, e.Edit.DocumentVersions, e.Edit.FileOperations,
#if LOOMO_EDITOR_HOST_API
                expectedTexts: e.Edit.ExpectedTexts));
#else
                expectedTexts: null));
#endif
        e.Applied = error is null;
        e.FailureReason = error;
        if (error is not null)
            Dispatcher.BeginInvoke(new Action(() => ShowRefactorStatus($"編集を適用できませんでした: {error}")));
    }

    private void ShowRefactorStatus(string message)
        => EditorSharedStatusBar?.UpdateStatus(message);

    /// <summary>C# のシグネチャ変更（LSP には無いので自前・§32.5）。
    /// メニューを開いた時点で読み取った宣言をそのまま使う（読み直すとキャレットが動いている）。</summary>
    private async Task ChangeSignatureAsync(MethodSignature signature)
    {
        var folders = _workspace.Folders;
        if (folders.Count == 0)
        {
            ShowRefactorStatus("ワークスペースが開かれていません。");
            return;
        }

        var dialog = new ChangeSignatureDialog(signature) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var currentText = FindOpenEditorText(signature.FilePath);
        var plan = currentText is null
            ? await new CSharpSignatureRefactoring(
                _lspWorkspace, folders, FindOpenEditorText).PlanAsync(signature, dialog.Result!)
            : await CSharpSignatureRefactoring.PlanWithSolutionAsync(
                _lspWorkspace, folders, FindOpenEditorText, _solutionModel?.Current,
                signature.FilePath, currentText, signature, dialog.Result!,
                FindOpenCSharpEditorTexts(), _csharpEditorConfig);
        if (plan.Error is { } planError)
        {
            ShowRefactorStatus(planError);
            return;
        }

        var error = ApplyLspWorkspaceEdit(plan.Changes, documentVersions: null, fileOperations: null,
            expectedTexts: plan.ExpectedTexts);
        if (error is not null)
        {
            ShowRefactorStatus($"シグネチャを変更できませんでした: {error}");
            return;
        }
        ShowRefactorStatus(plan.SkippedOutsideWorkspace > 0
            ? $"シグネチャを変更しました（{plan.SiteCount} 箇所）。" +
              $"ワークスペース外の {plan.SkippedOutsideWorkspace} 箇所は変更していません。"
            : $"シグネチャを変更しました（{plan.SiteCount} 箇所）。");
    }

    /// <summary>開いているタブが持つ最新テキスト（未保存を含む）。開いていなければ null。</summary>
    private string? FindOpenEditorText(string path)
        => _editorTabs.FirstOrDefault(tab =>
                tab.IsRealized && EditorPathMatches(tab.Control, path))
            ?.Control.Text;

    /// <summary>意味モデルを作るときに、開いているC#タブの未保存本文をすべて渡す。
    /// 対象ファイルだけを上書きすると、別ファイルの未保存method group／呼び出しを
    /// 古いディスク内容で安全確認してしまう。</summary>
    private IReadOnlyDictionary<string, string> FindOpenCSharpEditorTexts()
        => _editorTabs
            .Where(tab => tab.IsRealized &&
                string.Equals(Path.GetExtension(tab.Control.FilePath ?? ""), ".cs",
                    StringComparison.OrdinalIgnoreCase) &&
                tab.Control.FilePath is { Length: > 0 })
            .GroupBy(tab => Path.GetFullPath(tab.Control.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Control.Text,
                StringComparer.OrdinalIgnoreCase);
    /// <summary>コマンドの実効キー表記（利用者の割当を優先し、無ければカタログの既定、
    /// どちらも無ければ空文字＝キー表記の欄を出さない）。</summary>
    private string GestureFor(string commandId)
        => DescribeBinding(commandId) is { Length: > 0 } effective
            ? effective
            : CSharpEditorMenu.GestureFor(commandId) ?? "";
}
