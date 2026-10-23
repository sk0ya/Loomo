using sk0ya.Loomo.Core.Tasks;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 汎用タスクランナー（設計書 §31.17）。ワークスペース各フォルダーの
/// <c>.vscode/tasks.json</c> を読み、選んだタスクを<b>可視ターミナル</b>で実行して、出力を problem matcher で
/// 「問題」（Build・LSP と並ぶ発生源 Task）へ変換する。読み取り・実行順・照合は
/// <see cref="Core.Tasks"/> の純粋部品で、ここは入口（パレット）と Problems への流し込みだけ。
/// tasks.json は実行のたびに読み直す——編集してすぐ試せることの方が、毎回の数ミリ秒より大事。</summary>
public partial class ShellWindow
{
    private const string TaskCategory = "タスク";
    private WorkspaceTaskRunner? _taskRunner;
    private WorkspaceTaskRunner TaskRunner => _taskRunner ??= new WorkspaceTaskRunner(_terminal);

    private string? ActiveEditorFilePathForTasks
        => _activeEditorTab is { } tab ? (tab.IsRealized ? tab.Control.FilePath : tab.PeekFilePath) : null;

    /// <summary>「タスクを実行…」。既定のビルドタスクを先頭に、tasks.json のタスクをパレットへ並べる。</summary>
    private void OpenWorkspaceTasks()
    {
        var file = WorkspaceTaskCatalog.Load(_workspace.Folders);
        var activeFile = ActiveEditorFilePathForTasks;
        var preferred = _workspace.FolderForOrPrimary(activeFile);
        var items = WorkspaceTaskCatalog.Sort(file.Tasks, preferred)
            .Select(task => TaskPaletteItem(task, file.Tasks))
            .ToList();
        if (items.Count == 0)
        {
            items.Add(new PaletteCommand(TaskCategory, "実行できるタスクはありません", () => { })
            {
                Detail = "ワークスペースのフォルダーに .vscode/tasks.json を置くと、ここから実行できます。"
                    + Environment.NewLine + "（label・command・args・options.cwd・group・problemMatcher・dependsOn・inputs に対応）"
                    + WarningsDetail(file.Warnings),
            });
        }
        else if (file.Warnings.Count > 0)
        {
            items.Add(new PaletteCommand(TaskCategory, $"tasks.json の警告（{file.Warnings.Count}件）", () => { })
            {
                Detail = string.Join(Environment.NewLine, file.Warnings),
            });
        }
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>「既定のビルドタスクを実行」。決められない（既定が無く build が複数／0）ならタスク一覧を開く。</summary>
    private void RunDefaultBuildTask()
    {
        var file = WorkspaceTaskCatalog.Load(_workspace.Folders);
        var preferred = _workspace.FolderForOrPrimary(ActiveEditorFilePathForTasks);
        if (WorkspaceTaskCatalog.DefaultBuild(file.Tasks, preferred) is { } task)
        {
            _ = RunWorkspaceTaskAsync(task, file.Tasks);
            return;
        }
        ShowRefactorStatus(file.Tasks.Count == 0
            ? "tasks.json にタスクがありません。"
            : "既定のビルドタスクが決まっていません（group: { kind: \"build\", isDefault: true }）。一覧から選んでください。");
        OpenWorkspaceTasks();
    }

    private PaletteCommand TaskPaletteItem(WorkspaceTask task, IReadOnlyList<WorkspaceTask> all)
    {
        var multiRoot = _workspace.Folders.Count > 1;
        var title = multiRoot ? $"{task.Label}（{WorkspacePaths.FolderName(task.WorkspaceFolder)}）" : task.Label;
        return new PaletteCommand(TaskCategory, title, () => _ = RunWorkspaceTaskAsync(task, all))
        {
            Badge = task.IsDefaultBuild ? "既定ビルド"
                : task.IsBackground ? "watch"
                : task.Group is { Length: > 0 } group ? group : null,
            Detail = TaskDetail(task),
        };
    }

    private string TaskDetail(WorkspaceTask task)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.Detail)) lines.Add(task.Detail!);
        if (task.Command is { } command)
            lines.Add("コマンド: " + string.Join(' ', new[] { command }.Concat(task.Args)));
        lines.Add("作業フォルダー: " + (task.Cwd ?? _workspace.ToDisplayPath(task.WorkspaceFolder)));
        if (task.DependsOn.Count > 0) lines.Add("先に実行: " + string.Join("、", task.DependsOn));
        lines.Add(task.Matchers.Count > 0
            ? "問題への変換: " + string.Join("、", task.Matchers.Select(m => m.Owner))
            : "問題への変換: なし（problemMatcher 未指定）");
        if (task.IsBackground)
            lines.Add(task.Matchers.Any(m => m.Background is not null)
                ? "バックグラウンド（watch）：コンパイルが1回終わるたびに問題を差し替えます。Ctrl+C で止めるまで他のタスクは打ち込みません。"
                : "バックグラウンド（watch）：problemMatcher に background（beginsPattern / endsPattern）が無いので、問題へは止めたときに1回だけ変換します。");
        lines.Add("Enter で可視ターミナルで実行");
        return string.Join(Environment.NewLine, lines);
    }

    private static string WarningsDetail(IReadOnlyList<string> warnings)
        => warnings.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings);

    /// <summary>タスクを可視ターミナルで実行し、各タスクの終わりごとにその問題を差し替える。
    /// 途中でワークスペースを切り替えたら、結果は前のワークスペースのものなので流し込まない。</summary>
    private async Task RunWorkspaceTaskAsync(WorkspaceTask task, IReadOnlyList<WorkspaceTask> all)
    {
        if (TaskRunner.IsRunning)
        {
            ShowRefactorStatus("別のタスクを実行中です。終わってから実行してください。");
            return;
        }
        if (TaskRunner.ActiveWatch is { } watch)
        {
            ShowRefactorStatus($"watch タスク「{watch.Label}」が可視ターミナルで実行中です。Ctrl+C で止めるか、"
                + "パレットの「追跡をやめる」を選んでから実行してください。");
            return;
        }
        var workspaceId = _activeWorkspace?.Id;
        SetPaneVisible(PaneKind.Terminal, true);
        ShowRefactorStatus($"タスク「{task.Label}」を実行中…");
        try
        {
            var result = await TaskRunner.RunAsync(task, all, ActiveEditorFilePathForTasks, step =>
            {
                RememberTaskOutputMatching(step);
                if (_activeWorkspace?.Id == workspaceId)
                    PublishTaskProblems(step);
            },
            onWatchProblems: (watched, problems) =>
            {
                if (_activeWorkspace?.Id != workspaceId) return;
                PublishTaskProblems(watched, problems);
                var errors = problems.Count(p => p.Severity == TaskProblemSeverity.Error);
                var warnings = problems.Count(p => p.Severity == TaskProblemSeverity.Warning);
                ShowRefactorStatus(problems.Count == 0
                    ? $"watch「{watched.Label}」: 問題なし"
                    : $"watch「{watched.Label}」: エラー {errors} / 警告 {warnings}（「問題」タブ）");
            },
            onWatchFinished: (watched, exitCode) =>
            {
                if (_activeWorkspace?.Id != workspaceId) return;
                ShowRefactorStatus(exitCode is { } code
                    ? $"watch タスク「{watched.Label}」が終わりました（終了コード {code}）"
                    : $"watch タスク「{watched.Label}」の出力を追えなくなりました（ターミナルが閉じられました）");
            },
            askInput: AskTaskInputAsync);
            ShowRefactorStatus(result.Summary());
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"タスク「{task.Label}」を実行できませんでした: {ex.Message}");
        }
    }

    /// <summary><c>${input:…}</c> の値を訊く（§31.17.6）。promptString は入力ダイアログ（<c>password</c> なら伏せ字）、
    /// pickString はパレット——タスクを選んだのと同じ場所で、同じキー操作のまま続けて選べる。
    /// 取り消し（キャンセル・Escape）は null。</summary>
    private Task<string?> AskTaskInputAsync(TaskInput input, CancellationToken ct)
    {
        if (input.Kind != TaskInputKind.PickString)
            return Task.FromResult(InputDialog.Prompt(this, "タスクの入力", input.Prompt, input.Default ?? "",
                allowEmpty: true, password: input.Password));

        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var items = TaskInputs.OrderedOptions(input)
            .Select(option => new PaletteCommand(input.Prompt, option.Label, () => answer.TrySetResult(option.Value))
            {
                Badge = option.Value == input.Default ? "既定" : null,
                Detail = input.Prompt + Environment.NewLine
                    + (option.Label == option.Value ? "" : "値: " + option.Value + Environment.NewLine)
                    + "Enter で決定・Escape で取り消し（タスクは実行しません）",
            })
            .ToList();
        OpenCommandPalette(PaletteMode.Command, items);
        _paletteDismissed = () => answer.TrySetResult(null);
        ct.Register(() => answer.TrySetCanceled(ct));
        return answer.Task;
    }

    /// <summary>matcher を持つタスクだけが問題を書き換える（matcher の無いタスクを走らせても、
    /// 前回の結果を「0件＝きれい」と取り違えて消さない）。両方の IDE ペインへ同じものを流す（LSP と同じ）。</summary>
    private void PublishTaskProblems(TaskRunStep step)
    {
        if (step.Detached || step.Task.Matchers.Count == 0) return;
        PublishTaskProblems(step.Task, step.Problems);
    }

    private void PublishTaskProblems(WorkspaceTask task, IReadOnlyList<TaskProblem> problems)
    {
        var key = $"{task.WorkspaceFolder}|{task.Label}";
        _vm.Debug.Problems.SetTaskProblems(key, problems);
        _vm.TsIde.Problems.SetTaskProblems(key, problems);
    }

    /// <summary>watch の出力を追うのをやめる（コマンドは人間の端末で走り続ける）。シェル統合の無い端末では
    /// 終わりを検知できないので、次のタスクを打ち込めるようにする出口。</summary>
    private void StopWatchingTask()
    {
        if (_taskRunner?.ActiveWatch is not { } watch) return;
        _taskRunner.StopWatching();
        ShowRefactorStatus($"watch タスク「{watch.Label}」の追跡をやめました（ターミナルでは走り続けています）。");
    }
}
