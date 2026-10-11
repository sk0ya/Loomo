using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Tasks;

/// <summary>tasks.json の <c>inputs[].type</c>。</summary>
public enum TaskInputKind
{
    /// <summary><c>"promptString"</c>：自由入力の1行。</summary>
    PromptString,
    /// <summary><c>"pickString"</c>：<see cref="TaskInput.Options"/> から1つ選ぶ。</summary>
    PickString,
    /// <summary><c>"command"</c>：拡張機能のコマンドの戻り値。拡張機能が無いので実行時に断る。</summary>
    Command,
}

/// <summary>pickString の1項目。文字列だけの項目は表示名＝値。</summary>
public sealed record TaskInputOption(string Label, string Value);

/// <summary>
/// tasks.json の最上位 <c>inputs</c> の1項目（設計書 §31.17.6）。<c>${input:id}</c> で参照され、実行の直前に
/// 人間に訊いて値を決める。定義は tasks.json ごとなので、参照するタスクは同じファイルの定義だけを見る
/// （<see cref="WorkspaceTask.Inputs"/>）。
/// </summary>
public sealed record TaskInput
{
    public required string Id { get; init; }
    public TaskInputKind Kind { get; init; }
    /// <summary>訊くときの説明文（<c>description</c>）。</summary>
    public string? Description { get; init; }
    /// <summary><c>default</c>。promptString は入力欄の初期値、pickString は最初に選んでおく値。</summary>
    public string? Default { get; init; }
    public IReadOnlyList<TaskInputOption> Options { get; init; } = [];
    /// <summary><c>password</c>。入力欄は伏せる（値は可視ターミナルに打ち込まれるので、画面からは隠れない）。</summary>
    public bool Password { get; init; }

    /// <summary>訊くときの見出し（説明が無ければ id）。</summary>
    public string Prompt => string.IsNullOrWhiteSpace(Description) ? Id : Description!;
}

/// <summary>1回の実行で人間が答えた入力の値。同じ入力（同じ tasks.json の同じ id）は dependsOn の先で
/// 何度参照されても1回だけ訊く——VS Code と同じで、ビルドとデプロイで別の環境名を答えさせる余地を作らない。</summary>
public sealed class TaskInputValues
{
    private readonly Dictionary<TaskInput, string> _values = new(ReferenceEqualityComparer.Instance);

    public static TaskInputValues Empty { get; } = new();

    public void Set(TaskInput input, string value) => _values[input] = value;

    public bool TryGet(TaskInput input, out string value) => _values.TryGetValue(input, out value!);
}

/// <summary><c>${input:…}</c> の洗い出し（純粋関数・設計書 §31.17.6）。</summary>
public static class TaskInputs
{
    private static readonly Regex Reference = new(@"\$\{input:([^}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>実行順に並んだタスクが参照する入力を、最初に現れた順に重複なく返す。訊く前に全部を確かめるので、
    /// 3つ目の入力が定義されていないことが、2つ答えた後で分かることは無い。
    /// 未定義の id・<c>command</c> 型は <see cref="TaskConfigurationException"/>。</summary>
    public static IReadOnlyList<TaskInput> Required(IEnumerable<WorkspaceTask> plan)
    {
        var required = new List<TaskInput>();
        foreach (var task in plan)
        {
            if (task.Command is null) continue;   // dependsOn だけの複合タスクは何も打ち込まない
            foreach (var text in new[] { task.Command, task.Cwd }.Concat(task.Args))
            {
                if (text is null) continue;
                foreach (Match m in Reference.Matches(text))
                {
                    var id = m.Groups[1].Value;
                    if (!task.Inputs.TryGetValue(id, out var input))
                        throw new TaskConfigurationException(
                            $"「{task.Label}」の ${{input:{id}}} が tasks.json の inputs にありません。");
                    if (input.Kind == TaskInputKind.Command)
                        throw new TaskConfigurationException(
                            $"「{task.Label}」の入力「{id}」は type \"command\"（拡張機能のコマンド）なので使えません。");
                    if (!required.Contains(input, ReferenceEqualityComparer.Instance)) required.Add(input);
                }
            }
        }
        return required;
    }

    /// <summary>pickString の並び。<c>default</c> の項目を先頭に出す（Enter 1回で既定を選べる）。</summary>
    public static IReadOnlyList<TaskInputOption> OrderedOptions(TaskInput input)
    {
        var index = input.Default is null ? -1 : input.Options.ToList().FindIndex(o => o.Value == input.Default);
        if (index <= 0) return input.Options;
        var ordered = input.Options.ToList();
        var chosen = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(0, chosen);
        return ordered;
    }
}
