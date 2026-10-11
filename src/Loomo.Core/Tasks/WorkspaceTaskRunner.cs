using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tasks;

/// <summary>ワークスペースの全フォルダーから tasks.json を集める（設計書 §31.17）。</summary>
public static class WorkspaceTaskCatalog
{
    public const string RelativePath = ".vscode/tasks.json";

    /// <summary>フォルダーごとの <c>.vscode/tasks.json</c> を読む（無いフォルダーは飛ばす）。
    /// 警告はフォルダー名を前置して1つにまとめる。</summary>
    public static WorkspaceTasksFile Load(IReadOnlyList<string> folders)
    {
        var tasks = new List<WorkspaceTask>();
        var warnings = new List<string>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, ".vscode", "tasks.json");
            if (!File.Exists(path)) continue;
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(folder)}: tasks.json を読めません: {ex.Message}");
                continue;
            }
            var file = TasksJsonParser.Parse(json, folder);
            tasks.AddRange(file.Tasks);
            warnings.AddRange(file.Warnings.Select(w => folders.Count > 1 ? $"{Path.GetFileName(folder)}: {w}" : w));
        }
        return new WorkspaceTasksFile(tasks, warnings);
    }

    /// <summary>一覧に並べる順：既定のビルドタスク → ビルド → テスト → その他（各グループ内は定義順）。
    /// 既定のビルドタスクが複数フォルダーにあるときは <paramref name="preferredFolder"/>（いま開いている
    /// ファイルの持ち主）のものを先頭にする。</summary>
    public static IReadOnlyList<WorkspaceTask> Sort(IReadOnlyList<WorkspaceTask> tasks, string? preferredFolder)
    {
        int Rank(WorkspaceTask t) => t.IsDefaultBuild ? 0 : t.IsBuild ? 1
            : string.Equals(t.Group, "test", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        bool Preferred(WorkspaceTask t) => preferredFolder is not null &&
            string.Equals(t.WorkspaceFolder, preferredFolder, StringComparison.OrdinalIgnoreCase);
        return tasks
            .Select((task, index) => (task, index))
            .OrderBy(x => Rank(x.task))
            .ThenBy(x => Preferred(x.task) ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.task)
            .ToList();
    }

    /// <summary>「既定のビルドタスク」。<c>isDefault</c> の build を優先（持ち主フォルダーのもの→ほか）、
    /// 無ければ build グループがちょうど1つのときだけそれ。決められなければ null（呼び出し側が一覧を出す）。</summary>
    public static WorkspaceTask? DefaultBuild(IReadOnlyList<WorkspaceTask> tasks, string? preferredFolder)
    {
        var defaults = tasks.Where(t => t.IsDefaultBuild).ToList();
        if (defaults.Count > 0)
            return defaults.FirstOrDefault(t => preferredFolder is not null &&
                       string.Equals(t.WorkspaceFolder, preferredFolder, StringComparison.OrdinalIgnoreCase))
                   ?? defaults[0];
        var builds = tasks.Where(t => t.IsBuild).ToList();
        return builds.Count == 1 ? builds[0] : null;
    }
}

/// <summary>タスク1つ分の実行結果。</summary>
/// <param name="Problems">matcher が拾った問題（matcher 無し・バックグラウンドなら空）。</param>
/// <param name="Detached">バックグラウンド（watch）として起動だけして、終わりを待っていない。</param>
public sealed record TaskRunStep(
    TaskInvocation Invocation, int ExitCode, bool Success, IReadOnlyList<TaskProblem> Problems, bool Detached = false)
{
    public WorkspaceTask Task => Invocation.Task;
}

/// <summary>dependsOn を含めた1回の実行結果。<paramref name="Error"/> は実行前・途中で止めた理由。</summary>
public sealed record TaskRunResult(WorkspaceTask Root, IReadOnlyList<TaskRunStep> Steps, string? Error)
{
    public bool Success => Error is null && Steps.All(s => s.Success);

    /// <summary>ステータス表示用の1行（「タスク「build」: 失敗（終了コード 1）・エラー 2 / 警告 1」）。</summary>
    public string Summary()
    {
        var head = $"タスク「{Root.Label}」";
        if (Error is not null) return $"{head}: {Error}";
        if (Steps.Count > 0 && Steps[^1].Detached) return $"{head}: バックグラウンドで起動しました（ターミナル参照）";
        var failed = Steps.FirstOrDefault(s => !s.Success);
        var state = failed is null
            ? "成功"
            : ReferenceEquals(failed.Task, Root)
                ? $"失敗（終了コード {failed.ExitCode}）"
                : $"「{failed.Task.Label}」で失敗（終了コード {failed.ExitCode}）";
        var problems = Steps.SelectMany(s => s.Problems).ToList();
        if (problems.Count == 0) return $"{head}: {state}";
        var errors = problems.Count(p => p.Severity == TaskProblemSeverity.Error);
        var warnings = problems.Count(p => p.Severity == TaskProblemSeverity.Warning);
        return $"{head}: {state}・エラー {errors} / 警告 {warnings}（「問題」タブ）";
    }
}

/// <summary>
/// タスクを<b>人間の可視ターミナル</b>で実行し、出力に problem matcher を当てる（設計書 §31.17）。
///
/// <para>実行経路は <see cref="ITerminalService.RunCommandInVisibleTerminalAsync"/>——人間のターミナルの PTY に
/// 打ち込んで、OSC 133 の C〜D で区切られた出力と終了コードを受け取る（TS IDE の型チェックと同じ経路）。
/// 裏のプロセスで走らせて結果だけ Problems に出すこともできたが、それでは「部屋」の中で何が起きたかが
/// 人間に見えず、Ctrl+C で止めることも、出力をスクロールして読むこともできない。ビルドの出力は人間が読む
/// ものでもあるので、見える場所で走らせて、同じ出力を機械も読む。</para>
///
/// <para>1回に走らせるのは1つだけ（<see cref="IsRunning"/>）。可視ターミナルは1本で、続けて打ち込むと
/// 前のタスクの標準入力に入ってしまう。同じ理由で、watch（<c>isBackground</c>）が可視ターミナルを占有している間
/// （<see cref="ActiveWatch"/>）も次のタスクは打ち込まない。</para>
///
/// <para><b>watch の問題（§31.17.5）。</b>watch は終わらないので出力の全文は来ない。可視ターミナルの出力を論理行ごとに
/// 購読し（<see cref="ITerminalService.StartWatchedInVisibleTerminal"/>）、matcher の区切り（begins〜ends）で1回の
/// コンパイルが閉じるたびに、そのタスクの問題を差し替える（<see cref="BackgroundProblemCollector"/>）。watch が
/// 終わったら（Ctrl+C）購読を外し、最後のサイクルの問題は残す。</para>
/// </summary>
public sealed class WorkspaceTaskRunner(ITerminalService terminal)
{
    private int _running;
    private Watch? _watch;

    private sealed record Watch(WorkspaceTask Task, IDisposable Subscription);

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>可視ターミナルで走っていて、出力を追っている watch タスク（無ければ null）。</summary>
    public WorkspaceTask? ActiveWatch => _watch?.Task;

    /// <summary>watch の出力を追うのをやめる（コマンドは止めない）。ワークスペースの切替や、シェル統合が無くて
    /// 終わりを検知できない端末で、次のタスクを打ち込めるようにするため。</summary>
    public void StopWatching()
    {
        var watch = _watch;
        _watch = null;
        watch?.Subscription.Dispose();
    }

    /// <param name="onStep">各タスクが終わるたびに呼ぶ（Problems をタスクごとに差し替えるため）。</param>
    /// <param name="onWatchProblems">watch の1サイクルが閉じるたびに、そのタスクの問題で呼ぶ（UI スレッド）。</param>
    /// <param name="onWatchFinished">watch が終わったとき（終了コード／不明なら null）に呼ぶ（UI スレッド）。</param>
    /// <param name="askInput"><c>${input:…}</c> の値を人間に訊く（§31.17.6）。null を返したら取り消し＝何も打ち込まない。
    /// 訊くのは依存も含めて1つも打ち込む前——1つ目のビルドが走った後で2つ目の問いを取り消されると、半端に終わる。</param>
    public async Task<TaskRunResult> RunAsync(
        WorkspaceTask task, IReadOnlyList<WorkspaceTask> all, string? activeFile,
        Action<TaskRunStep>? onStep = null, CancellationToken ct = default,
        Action<WorkspaceTask, IReadOnlyList<TaskProblem>>? onWatchProblems = null,
        Action<WorkspaceTask, int?>? onWatchFinished = null,
        Func<TaskInput, CancellationToken, Task<string?>>? askInput = null)
    {
        if (_watch is { } running)
            return new TaskRunResult(task, [],
                $"watch タスク「{running.Task.Label}」が可視ターミナルで実行中です。Ctrl+C で止めてから実行してください。");
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return new TaskRunResult(task, [], "別のタスクを実行中です。終わってから実行してください。");
        try
        {
            List<TaskInvocation> plan;
            try
            {
                var order = TaskPlanner.Order(task, all);
                if (order.Take(order.Count - 1).FirstOrDefault(t => t.IsBackground) is { } background)
                    throw new TaskConfigurationException(
                        $"バックグラウンドのタスク「{background.Label}」は dependsOn に使えません（終わらないので後のタスクが打ち込めません）。");
                var required = TaskInputs.Required(order);
                var inputs = new TaskInputValues();
                foreach (var input in required)
                {
                    if (askInput is null)
                        throw new TaskConfigurationException($"入力「{input.Id}」を訊く手段がありません。");
                    if (await askInput(input, ct) is not { } value)
                        return new TaskRunResult(task, [], $"入力「{input.Prompt}」を取り消したので実行しませんでした。");
                    inputs.Set(input, value);
                }
                plan = order.Select(t => TaskCommandBuilder.Build(t, activeFile, inputs)).ToList();
            }
            catch (TaskConfigurationException ex)
            {
                return new TaskRunResult(task, [], ex.Message);
            }

            var steps = new List<TaskRunStep>();
            foreach (var invocation in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (invocation.TerminalLine is null) continue;   // dependsOn だけの複合タスク

                if (invocation.Task.IsBackground)
                {
                    if (StartWatch(invocation, onWatchProblems, onWatchFinished) is not { } subscription)
                        return new TaskRunResult(task, steps, "可視ターミナルが接続されていません。ターミナルを開いてから実行してください。");
                    _watch = new Watch(invocation.Task, subscription);
                    var detached = new TaskRunStep(invocation, 0, true, [], Detached: true);
                    steps.Add(detached);
                    onStep?.Invoke(detached);
                    continue;
                }

                var result = await terminal.RunCommandInVisibleTerminalAsync(invocation.TerminalLine, ct);
                var problems = invocation.Task.Matchers.Count == 0
                    ? (IReadOnlyList<TaskProblem>)[]
                    : ProblemMatcherEngine.Match(result.Output, invocation.Task.Matchers,
                        new ProblemMatchContext(invocation.Task.WorkspaceFolder, invocation.Cwd));
                var step = new TaskRunStep(invocation, result.ExitCode, result.ExitCode == 0 && result.Success, problems);
                steps.Add(step);
                onStep?.Invoke(step);
                // VS Code と同じく、依存が失敗したら後続は走らせない。
                if (!step.Success) break;
            }
            return new TaskRunResult(task, steps, null);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private IDisposable? StartWatch(TaskInvocation invocation,
        Action<WorkspaceTask, IReadOnlyList<TaskProblem>>? onProblems, Action<WorkspaceTask, int?>? onFinished)
    {
        var watched = invocation.Task;
        var collector = watched.Matchers.Count == 0 ? null
            : new BackgroundProblemCollector(watched.Matchers,
                new ProblemMatchContext(watched.WorkspaceFolder, invocation.Cwd),
                problems => onProblems?.Invoke(watched, problems));
        IDisposable? subscription = null;
        subscription = terminal.StartWatchedInVisibleTerminal(invocation.TerminalLine!,
            line => collector?.OnLine(line),
            exitCode =>
            {
                collector?.Complete();
                if (_watch is { } current && ReferenceEquals(current.Subscription, subscription)) _watch = null;
                subscription?.Dispose();
                onFinished?.Invoke(watched, exitCode);
            });
        return subscription;
    }
}
