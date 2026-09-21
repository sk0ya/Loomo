using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Markdown;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct SelectionCompareMenuEntry(string Label, string ToolTip, Action Run);

/// <summary>選択テキストの送信先メニューを組み立てる。</summary>
internal static class SelectionActionMenuBuilder
{
    public static MenuItem? BuildMarkdownTableMenuItem(
        string documentPath, string text, int caretLine, Action edit, Action insert)
    {
        if (!MarkdownBlockDiff.IsMarkdownPath(documentPath))
            return null;

        var inTable = MarkdownTableDocumentEditor.TryFindAtCaret(text, caretLine, out _);
        var item = new MenuItem
        {
            Header = inTable ? "テーブルを VGrid で編集…" : "テーブルを挿入…",
        };
        item.Click += (_, _) => (inTable ? edit : insert)();
        return item;
    }

    public static MenuItem? BuildMarkdownPathRefactorMenuItem(
        IWorkspaceService workspace, string text, int line, int column, string documentPath,
        Action<MarkdownPathActionTarget> refactor)
    {
        if (!SelectionActionTargetResolver.TryResolveMarkdownPathAtCaret(
                workspace, text, line, column, documentPath, out var target))
            return null;

        var item = new MenuItem
        {
            Header = $"リンク先を移動・参照を更新…（{Path.GetFileName(target.SourcePath)}）",
        };
        item.Click += (_, _) => refactor(target);
        return item;
    }

    public static MenuItem BuildGitMenuItem(Action showHistory, Action showBlame)
    {
        var git = new MenuItem { Header = "Git" };
        var history = new MenuItem { Header = "このファイルの履歴を表示" };
        history.Click += (_, _) => showHistory();
        git.Items.Add(history);
        var blame = new MenuItem { Header = "行ごとの最終更新を表示（Blame）", InputGestureText = ":Gblame" };
        blame.Click += (_, _) => showBlame();
        git.Items.Add(blame);
        return git;
    }

    public static void AddBlameCommitMenuItems(
        ContextMenu menu, string? commitHash, Action showDiff, Action showHistory)
    {
        var shortHash = commitHash is { Length: > 7 } hash ? hash[..7] : commitHash;
        var diff = new MenuItem { Header = $"Diff で差分を表示（{shortHash}）" };
        diff.Click += (_, _) => showDiff();
        menu.Items.Add(diff);
        var history = new MenuItem { Header = "Git ペインでこのファイルの履歴を表示" };
        history.Click += (_, _) => showHistory();
        menu.Items.Add(history);
    }

    public static MenuItem BuildRunScriptMenuItem(string path, bool terminalAvailable, Action run)
    {
        var item = new MenuItem
        {
            Header = $"ターミナルで実行（{Path.GetFileName(path)}）",
            IsEnabled = terminalAvailable,
        };
        item.Click += (_, _) => run();
        return item;
    }

    public static MenuItem? BuildDetachedWindowLinkMenuItem(LinkOpenTarget target, Action open)
    {
        if (SelectionActionPresentation.DetachedWindowLinkHeader(target) is not { } header)
            return null;
        var item = new MenuItem { Header = header, ToolTip = target.Value };
        item.Click += (_, _) => open();
        return item;
    }

    public static MenuItem BuildDebugMenuItem(
        DebugManagerViewModelBase manager, string path, int line0, Action editBreakpointCondition)
    {
        var root = new MenuItem { Header = "デバッグ" };
        var editCondition = new MenuItem { Header = "ブレークポイントの条件を編集…" };
        editCondition.Click += (_, _) => editBreakpointCondition();
        root.Items.Add(editCondition);

        if (!manager.IsStopped)
            return root;

        var runTo = new MenuItem { Header = "カーソル行まで実行" };
        runTo.Click += (_, _) => _ = manager switch
        {
            TsDebugViewModel ts => ts.Launch.RunToCursorAsync(path, line0),
            DebugViewModel dotnet => dotnet.Launch.RunToCursorAsync(path, line0),
            _ => Task.CompletedTask,
        };
        root.Items.Add(runTo);

        if (manager is not DebugViewModel debug)
            return root;

        if (debug.Launch.SupportsSetNextStatement)
        {
            var setNext = new MenuItem { Header = "次のステートメントに設定（この行へ）" };
            setNext.Click += (_, _) => _ = debug.Launch.SetNextStatementAsync(path, line0);
            root.Items.Add(setNext);
        }
        if (debug.Launch.SupportsStepInTargets)
            root.Items.Add(BuildStepInTargetsMenu(debug.Launch));
        return root;
    }

    private static MenuItem BuildStepInTargetsMenu(DebugLaunchViewModel debug)
    {
        var parent = new MenuItem { Header = "特定の関数にステップ イン" };
        parent.Items.Add(new MenuItem { Header = "(読み込み中…)", IsEnabled = false });
        parent.SubmenuOpened += async (_, _) =>
        {
            var targets = await debug.GetStepInTargetsAsync();
            parent.Items.Clear();
            if (targets.Count == 0)
            {
                parent.Items.Add(new MenuItem { Header = "(候補がありません)", IsEnabled = false });
                return;
            }
            foreach (var target in targets)
            {
                var item = new MenuItem { Header = target.Label };
                item.Click += (_, _) => _ = debug.StepIntoTargetAsync(target);
                parent.Items.Add(item);
            }
        };
        return parent;
    }

    public static void AddMenuGroup(ContextMenu menu, Action<ContextMenu> build)
    {
        var before = menu.Items.Count;
        build(menu);
        if (menu.Items.Count > before && before > 0)
            menu.Items.Insert(before, new Separator());
    }

    public static MenuItem? BuildEditorSendMenuItem(
        IWorkspaceService workspace,
        string selectedText,
        bool hasSelection,
        string? workingDirectory,
        string? currentDocumentPath,
        Action<SourceLocation> openLocation)
    {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText)
            || !SourceLocationResolver.TryResolve(
                workspace, selectedText, workingDirectory, currentDocumentPath, out var location))
            return null;

        var (header, toolTip) = SelectionActionPresentation.EditorSendItem(location);
        var item = new MenuItem { Header = header, ToolTip = toolTip };
        item.Click += (_, _) => openLocation(location);
        return item;
    }

    public static void AddSelectionMenuItems(
        ContextMenu menu,
        string selectedText,
        bool hasSelection,
        MenuItem? editorItem,
        MenuItem? diffItem,
        bool aiAvailable,
        Action askAi,
        Action<string> searchInBrowser,
        Action<ContextMenu, string> addWorkflowItems)
    {
        var hasText = hasSelection && !string.IsNullOrWhiteSpace(selectedText);
        if (!hasText && editorItem is null && diffItem is null)
            return;
        if (hasText)
        {
            var ask = new MenuItem
            {
                Header = "AIへ送る",
                ToolTip = "選択テキストについてAIに尋ねる",
                IsEnabled = aiAvailable,
            };
            ask.Click += (_, _) => askAi();
            menu.Items.Add(ask);
            var search = new MenuItem
            {
                Header = "ブラウザへ送る",
                ToolTip = "選択テキストをブラウザペインで検索する",
            };
            search.Click += (_, _) => searchInBrowser(selectedText);
            menu.Items.Add(search);
        }
        if (editorItem is not null)
            menu.Items.Add(editorItem);
        if (diffItem is not null)
            menu.Items.Add(diffItem);
        if (hasText)
            addWorkflowItems(menu, selectedText);
    }

    public static MenuItem? BuildDiffSendMenu(IReadOnlyList<SelectionCompareMenuEntry> entries)
    {
        if (entries.Count == 0)
            return null;
        if (entries.Count == 1)
        {
            var only = entries[0];
            var flat = new MenuItem { Header = $"Diffへ送る（{only.Label}）", ToolTip = only.ToolTip };
            flat.Click += (_, _) => only.Run();
            return flat;
        }
        var parent = new MenuItem
        {
            Header = "Diffへ送る",
            ToolTip = "クリップボードや保存済みの内容と Diff ペインで見比べる",
        };
        foreach (var entry in entries)
        {
            var item = new MenuItem { Header = entry.Label, ToolTip = entry.ToolTip };
            item.Click += (_, _) => entry.Run();
            parent.Items.Add(item);
        }
        return parent;
    }

    public static void AddWorkflowMenuItems(
        ContextMenu menu,
        string input,
        IEnumerable<(string Id, string Name)> workflows,
        bool aiAvailable,
        Action<string, string> runWorkflow)
    {
        var entries = workflows.ToArray();
        if (entries.Length == 0)
            return;
        var parent = new MenuItem
        {
            Header = "AIワークフローへ送る",
            ToolTip = "選択テキストを入力にしてワークフローを実行する",
            IsEnabled = aiAvailable,
        };
        foreach (var workflow in entries)
        {
            var id = workflow.Id;
            var item = new MenuItem { Header = workflow.Name };
            item.Click += (_, _) => runWorkflow(id, input);
            parent.Items.Add(item);
        }
        menu.Items.Add(parent);
    }
}
