namespace sk0ya.Loomo.Core.Tools;

/// <summary>
/// 委譲ツール <c>delegate_task</c> の契約（ツール名・canonical な引数キー・キー別名）。
/// <see cref="WebSearchContract"/>／<see cref="PwshContract"/> と同じ流儀で、定義・正規化・実行が同じ語彙を共有する。
/// 自己完結したサブタスクを <c>task</c>（必須）で、ワーカーに渡す最小限の入力を <c>context</c>（任意）で受け取り、
/// 隔離されたサブエージェント（まっさらな会話）に実行させ、最終結果だけを返す。
/// </summary>
public static class DelegateTaskContract
{
    /// <summary>ツール名。サブエージェントには<b>見せない</b>（無限委譲の防止）。レジストリには最後に登録し、
    /// 「delegate を除いたツール集合」がフル集合の真の接頭辞になるようにして、ウォームアップ済み
    /// <c>[system][tools]</c> プレフィックスをサブ実行でもほぼ再利用できるようにする。</summary>
    public const string ToolName = "delegate_task";

    /// <summary>canonical な引数キー（正規化後は必ずこのキーに揃う）。</summary>
    public const string TaskArg = "task";

    /// <summary>ワーカーへ渡す任意の参考入力（メイン会話は見えないので必要な文脈はここで明示的に渡す）。</summary>
    public const string ContextArg = "context";

    /// <summary>task を表す引数キーの揺れ。小モデルは instruction/subtask 等で送ることがある。先頭が canonical。</summary>
    public static readonly string[] TaskKeys =
        { TaskArg, "instruction", "subtask", "goal", "objective", "prompt", "request" };

    /// <summary>context を表す引数キーの揺れ。先頭が canonical。</summary>
    public static readonly string[] ContextKeys =
        { ContextArg, "input", "data", "info", "background", "reference", "notes" };
}
