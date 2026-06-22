using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// <see cref="ISubAgentRunner"/> の実装。<b>まっさらな <see cref="Conversation"/></b> に対して
/// <see cref="AgentOrchestrator.RunTurnAsync"/> を 1 ターン回し、最終テキストだけを取り出す。
///
/// 位置づけは「<b>ツールの実装が内部で AI を活用する</b>」ための基盤。エージェントに委譲ツールを<i>提示</i>して
/// AI に委譲を選ばせるのではなく、ツール（<see cref="sk0ya.Loomo.Core.Tools.IAgentTool"/>）の <c>ExecuteAsync</c>
/// が必要に応じてこれを呼び、隔離された AI サブタスク（要約・分類・整形・調査など）を回して結果だけ受け取る。
/// メイン会話の履歴は運ばないので、大きな中間出力がメイン側に積もらない（狙いは docs/エージェントループ知見.md §2.1）。
///
/// 依存（<see cref="AgentOrchestrator"/>）は <see cref="IServiceProvider"/> 経由で<b>実行時に遅延解決</b>する。
/// これを注入するツールがレジストリに載ると、ツール → 本実行器 → オーケストレータ → <see cref="sk0ya.Loomo.Core.Tools.ToolRegistry"/>
/// →（そのツール）という DI 構築の循環が生じうるため、遅延解決で断つ（サービスロケーションはこの 1 箇所に閉じ込める）。
/// </summary>
public sealed class SubAgentRunner : ISubAgentRunner
{
    private readonly IServiceProvider _provider;

    public SubAgentRunner(IServiceProvider provider) => _provider = provider;

    public async Task<SubAgentResult> RunAsync(string task, string? context, CancellationToken ct)
    {
        var orchestrator = _provider.GetRequiredService<AgentOrchestrator>();

        var conversation = new Conversation();   // 履歴を運ばない＝隔離された最小コンテキスト
        var prompt = ComposePrompt(task, context);
        var sessionId = "subagent-" + Guid.NewGuid().ToString("N");

        string finalText = "";
        string? errorMessage = null;
        var toolsUsed = new List<string>();

        // ツール集合はオーケストレータ既定（全登録ツール）をそのまま使う。現状 AI を内部利用するツールは
        // 無いため再帰の恐れはない。将来そうしたツールを登録する場合は、ここで自己参照ツールを除外するか
        // 深さガードを足すこと（無限のサブエージェント生成を防ぐため）。
        await foreach (var ev in orchestrator.RunTurnAsync(conversation, prompt, sessionId, ct))
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
