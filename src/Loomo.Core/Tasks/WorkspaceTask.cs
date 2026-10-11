namespace sk0ya.Loomo.Core.Tasks;

/// <summary>タスクの実行形態（tasks.json の <c>type</c>）。</summary>
public enum WorkspaceTaskKind
{
    /// <summary><c>"shell"</c>：コマンド行をシェルにそのまま渡す（引数は必要な分だけ引用する）。</summary>
    Shell,
    /// <summary><c>"process"</c>：実行ファイル＋引数。シェルの解釈を受けないよう全部引用して <c>&amp;</c> で呼ぶ。</summary>
    Process,
}

/// <summary>
/// ワークスペースの1タスク（<c>.vscode/tasks.json</c> の1項目を読んだもの・設計書 §31.17）。
///
/// <para><b>変数（<c>${workspaceFolder}</c> 等）は未展開のまま持つ。</b><c>${file}</c> のように実行の瞬間に
/// しか決まらないものがあるので、展開は <see cref="TaskCommandBuilder"/> が実行時に行う。</para>
/// </summary>
public sealed record WorkspaceTask
{
    /// <summary>表示名かつ <c>dependsOn</c> の参照名。</summary>
    public required string Label { get; init; }
    /// <summary>このタスクを定義した tasks.json を持つワークスペースフォルダー（<c>${workspaceFolder}</c>）。</summary>
    public required string WorkspaceFolder { get; init; }
    public WorkspaceTaskKind Kind { get; init; } = WorkspaceTaskKind.Shell;
    /// <summary>コマンド。<c>dependsOn</c> だけの複合タスクでは null。</summary>
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    /// <summary><c>options.cwd</c>（未展開）。null なら <see cref="WorkspaceFolder"/>。</summary>
    public string? Cwd { get; init; }
    /// <summary><c>group</c> の種類（<c>build</c> / <c>test</c> / null）。</summary>
    public string? Group { get; init; }
    /// <summary><c>group.isDefault</c>。</summary>
    public bool IsDefault { get; init; }
    /// <summary><c>isBackground</c>（watch 系）。終わらないので出力を待たず、問題への変換もしない。</summary>
    public bool IsBackground { get; init; }
    public IReadOnlyList<ProblemMatcher> Matchers { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    /// <summary><c>detail</c>（一覧の補足）。</summary>
    public string? Detail { get; init; }
    /// <summary>定義元の tasks.json の <c>inputs</c>（id → 定義）。<c>${input:id}</c> はここだけを引く。</summary>
    public IReadOnlyDictionary<string, TaskInput> Inputs { get; init; } = EmptyInputs;

    private static readonly IReadOnlyDictionary<string, TaskInput> EmptyInputs = new Dictionary<string, TaskInput>();

    public bool IsBuild => string.Equals(Group, "build", StringComparison.OrdinalIgnoreCase);
    public bool IsDefaultBuild => IsBuild && IsDefault;
}

/// <summary>tasks.json の読み取り結果。読めなかった項目・未対応の指定は <see cref="Warnings"/> に積み、
/// 読めた分だけでも使えるようにする（1項目の誤りで全タスクが消えると、直す場所も見えない）。</summary>
public sealed record WorkspaceTasksFile(IReadOnlyList<WorkspaceTask> Tasks, IReadOnlyList<string> Warnings)
{
    public static WorkspaceTasksFile Empty { get; } = new([], []);
}

public enum TaskProblemSeverity { Error, Warning, Information, Hint }

/// <summary>problem matcher がタスク出力から拾った1件。位置は<b>1始まり</b>、パスは絶対化済み。
/// <paramref name="Code"/> は取れなければ null（表示側で発生源名を代用する）。</summary>
public sealed record TaskProblem(
    string FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    TaskProblemSeverity Severity,
    string? Code,
    string Message,
    string Owner);
