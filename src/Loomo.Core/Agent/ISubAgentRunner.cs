using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// 隔離されたサブエージェントを 1 回走らせる実行器。<c>delegate_task</c> ツールの「手足」で、
/// メイン会話の履歴を運ばずに自己完結したサブタスクを実行し、<b>最終結果だけ</b>を返す。
/// これにより大きな中間出力（ファイル全文・検索ダンプ・長いコマンド出力）がメイン会話に積もらず、
/// decode コスト・トリミング・モデルの混乱を抑える（設計の狙いは docs/エージェントループ知見.md §2.1）。
/// </summary>
public interface ISubAgentRunner
{
    /// <summary>サブタスクを隔離実行する。</summary>
    /// <param name="task">ワーカーへの自己完結した指示。</param>
    /// <param name="context">ワーカーが必要とする最小限の入力（メイン会話は見えないので明示的に渡す）。null可。</param>
    Task<SubAgentResult> RunAsync(string task, string? context, CancellationToken ct);
}

/// <summary>サブエージェント実行の結果。<see cref="Text"/> がメイン会話へ戻る（合成の入力になる）コンパクトな成果物。</summary>
public sealed record SubAgentResult(string Text, bool IsError, IReadOnlyList<string> ToolsUsed);
