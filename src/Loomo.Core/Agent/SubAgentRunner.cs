using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// <see cref="ISubAgentRunner"/> の実装。<b>まっさらな <see cref="Conversation"/></b> に対して
/// <see cref="AgentOrchestrator.RunTurnAsync"/> を 1 ターン回し、最終テキストだけを取り出す。
///
/// 依存（<see cref="AgentOrchestrator"/>／<see cref="ToolRegistry"/>）は <see cref="IServiceProvider"/> 経由で
/// <b>実行時に遅延解決</b>する。委譲ツール → 本実行器 → オーケストレータ → <see cref="ToolRegistry"/> →（委譲ツール）
/// という DI 構築の循環を断つため。サービスロケーションは合成境界のこの 1 箇所に閉じ込める。
///
/// サブエージェントには <c>delegate_task</c> を<b>提示しない</b>（無限委譲の防止）。<c>delegate_task</c> は
/// レジストリ最後尾に登録されているため、除外後の集合はフル集合の真の接頭辞＝ウォームアップ済み
/// <c>[system][tools]</c> プレフィックスをほぼ再利用できる（分岐は tools 配列末尾のみ）。
/// </summary>
public sealed class SubAgentRunner : ISubAgentRunner
{
    private readonly IServiceProvider _provider;

    public SubAgentRunner(IServiceProvider provider) => _provider = provider;

    public async Task<SubAgentResult> RunAsync(string task, string? context, CancellationToken ct)
    {
        var orchestrator = _provider.GetRequiredService<AgentOrchestrator>();
        var registry = _provider.GetRequiredService<ToolRegistry>();

        // サブエージェントへ提示するツール = 全ツール − delegate_task（再帰防止＆プレフィックス再利用の両立）。
        var subDefinitions = registry.Definitions
            .Where(d => d.Name != DelegateTaskContract.ToolName)
            .ToList();

        var conversation = new Conversation();   // 履歴を運ばない＝隔離された最小コンテキスト
        var prompt = ComposePrompt(task, context);
        var sessionId = "subagent-" + Guid.NewGuid().ToString("N");

        string finalText = "";
        string? errorMessage = null;
        var toolsUsed = new List<string>();

        await foreach (var ev in orchestrator.RunTurnAsync(
                           conversation, prompt, sessionId, ct, toolDefinitionsOverride: subDefinitions))
        {
            switch (ev)
            {
                case ToolUseRequested r:
                    toolsUsed.Add(r.ToolUse.Name);
                    break;
                case AgentError e:
                    errorMessage = e.Message;
                    break;
                case TurnCompleted tc:
                    finalText = tc.FinalText ?? "";
                    break;
            }
        }

        return errorMessage is not null
            ? new SubAgentResult(errorMessage, IsError: true, toolsUsed)
            : new SubAgentResult(finalText, IsError: false, toolsUsed);
    }

    /// <summary>サブタスクの user プロンプトを組み立てる。system プロンプトは共有・不変なので
    /// 追加の枠組み文は最小限にとどめる（過剰指示は小モデルを却って混乱させる）。</summary>
    private static string ComposePrompt(string task, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return task;

        var sb = new StringBuilder(task.Length + context.Length + 64);
        sb.Append(task);
        sb.Append("\n\n--- 参考情報（このサブタスクのための入力） ---\n");
        sb.Append(context);
        return sb.ToString();
    }
}
