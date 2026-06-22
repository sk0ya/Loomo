using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>
/// 自己完結したサブタスクを隔離されたサブエージェント（まっさらな会話）に委譲する構造化ツール。
/// ワーカーは<b>メイン会話とその履歴を見ない</b>最小コンテキストで起動し、同じファイル/ターミナル/検索ツールを
/// 使ってタスクを実行し、<b>最終結果だけ</b>を返す。大きな中間出力（ファイル全文・検索ダンプ・長いコマンド出力）を
/// メイン会話に積もらせないための「手足」で、実体は <see cref="ISubAgentRunner"/>。
///
/// このツール自体は副作用を持たない（承認不要）。ワーカー内部のツール（run_powershell/write_file/edit_file）は
/// 従来どおり安全評価＋承認カードを個別に通る。
/// </summary>
public sealed class DelegateTaskTool : IAgentTool
{
    private readonly ISubAgentRunner _runner;

    public DelegateTaskTool(ISubAgentRunner runner) => _runner = runner;

    public string Name => DelegateTaskContract.ToolName;

    // 委譲自体は副作用なし。実際の副作用（コマンド/書込）はワーカー内部の各ツールが個別に承認する。
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "Delegate a self-contained subtask to an isolated worker agent. The worker starts with a FRESH, minimal "
        + "context: it does NOT see this conversation or its history, and can use the same file/terminal/search tools. "
        + "It returns only its final result. Use this to keep large intermediate output (file contents, search dumps, "
        + "long command output) out of the main conversation: pass a focused 'task' plus any data the worker needs in "
        + "'context'. Do NOT delegate a trivial single step you can do directly.",
        ToolDefinition.ObjectSchema(
            (DelegateTaskContract.TaskArg, "string",
                "The focused, self-contained instruction for the worker agent.", true),
            (DelegateTaskContract.ContextArg, "string",
                "Optional minimal context/data the worker needs (it cannot see the main conversation).", false)));

    public string DescribeInvocation(JsonElement args)
        => $"delegate_task: {Truncate(args.GetString(DelegateTaskContract.TaskArg), 80)}";

    /// <summary>task/context の別名キーを吸収して canonical な <c>{"task":..,"context":..}</c> へ寄せる。</summary>
    public JsonElement NormalizeArguments(JsonElement arguments)
    {
        var task = arguments.GetStringAny(DelegateTaskContract.TaskKeys);
        if (string.IsNullOrWhiteSpace(task))
            task = arguments.SingleStringValue();   // 想定外キー1個だけなら本文として拾う最後の砦
        var context = arguments.GetStringAny(DelegateTaskContract.ContextKeys);

        var dict = new Dictionary<string, string> { [DelegateTaskContract.TaskArg] = task };
        if (!string.IsNullOrWhiteSpace(context))
            dict[DelegateTaskContract.ContextArg] = context;
        return JsonSerializer.SerializeToElement(dict);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var task = args.GetString(DelegateTaskContract.TaskArg);
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Error(
                "task が空です。arguments に {\"task\":\"<実行させたい自己完結したサブタスク>\"} を入れて呼び出してください。"
                + "ワーカーはメイン会話を見られないので、必要な文脈は context に明示してください。");

        var context = args.GetString(DelegateTaskContract.ContextArg);
        var result = await _runner.RunAsync(task, string.IsNullOrWhiteSpace(context) ? null : context, ct);

        if (result.IsError)
            return ToolResult.Error($"サブタスクは失敗しました: {result.Text}");

        return ToolResult.Ok(string.IsNullOrWhiteSpace(result.Text)
            ? "サブタスクは完了しましたが、出力テキストはありませんでした。"
            : result.Text);
    }

    private static string Truncate(string? s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
