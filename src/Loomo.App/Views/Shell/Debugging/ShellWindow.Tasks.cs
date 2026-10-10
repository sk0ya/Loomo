using sk0ya.Loomo.Core.Tasks;

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
                    + Environment.NewLine + "（label・command・args・options.cwd・group・problemMatcher・dependsOn に対応）"
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
        var title = multiRoot ? $"{task.Label}（{Path.GetFileName(task.WorkspaceFolder.TrimEnd('\\', '/'))}）" : task.Label;
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
        if (task.IsBackground) lines.Add("バックグラウンド（watch）：起動だけして終わりを待たず、問題へは変換しません。");
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
        var workspaceId = _activeWorkspace?.Id;
        SetPaneVisible(PaneKind.Terminal, true);
        ShowRefactorStatus($"タスク「{task.Label}」を実行中…");
        try
        {
            var result = await TaskRunner.RunAsync(task, all, ActiveEditorFilePathForTasks, step =>
            {
                if (_activeWorkspace?.Id == workspaceId)
                    PublishTaskProblems(step);
            });
            ShowRefactorStatus(result.Summary());
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"タスク「{task.Label}」を実行できませんでした: {ex.Message}");
        }
    }

    /// <summary>matcher を持つタスクだけが問題を書き換える（matcher の無いタスクを走らせても、
    /// 前回の結果を「0件＝きれい」と取り違えて消さない）。両方の IDE ペインへ同じものを流す（LSP と同じ）。</summary>
    private void PublishTaskProblems(TaskRunStep step)
    {
        if (step.Detached || step.Task.Matchers.Count == 0) return;
        var key = $"{step.Task.WorkspaceFolder}|{step.Task.Label}";
        _vm.Debug.Problems.SetTaskProblems(key, step.Problems);
        _vm.TsIde.Problems.SetTaskProblems(key, step.Problems);
    }
}
