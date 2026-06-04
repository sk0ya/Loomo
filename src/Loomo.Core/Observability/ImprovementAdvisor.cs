using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Core.Observability;

/// <summary>ツール1件の名称・説明（改善提案へ渡す素材）。</summary>
public sealed record ToolInfo(string Name, string Description);

/// <summary>改善提案の入力。集計済みメトリクスと失敗サンプルを与える（UI層が組み立てる）。</summary>
public sealed record AdvisorInput(
    string CurrentSystemPrompt,
    IReadOnlyList<ToolInfo> Tools,
    SessionMetrics? Single,
    CrossSessionMetrics? Cross,
    IReadOnlyList<string> FailureSamples);

/// <summary>
/// トレース集計を AI 自身に渡し、エージェントループ・ツール運用の改善提案を
/// 生成する（設計書 §20 の発展）。現在プロバイダのクライアントで一度きりの応答を取り、テキストを流す。
/// <para>
/// クライアントは共有 <c>AiSettings.SystemPrompt</c> を system ロールに注入するため、メタ指示は
/// user メッセージに置き、評価対象のシステムプロンプトはその中へ明示的に埋め込む。ツール定義は空で渡し、
/// 解析中にツールを実行させない。
/// </para>
/// </summary>
public sealed class ImprovementAdvisor
{
    private readonly IAiClientFactory _aiFactory;

    public ImprovementAdvisor(IAiClientFactory aiFactory) => _aiFactory = aiFactory;

    /// <summary>改善提案レポートをテキストの逐次断片として生成する。</summary>
    public async IAsyncEnumerable<string> AnalyzeAsync(
        AdvisorInput input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ai = _aiFactory.ResolveCurrent();
        var conversation = new Conversation();
        conversation.AddUser(BuildPrompt(input));

        var sawText = false;
        var notedToolUse = false;
        await foreach (var ev in ai.StreamAsync(conversation, Array.Empty<ToolDefinition>(), ct))
        {
            switch (ev)
            {
                case TextDelta delta:
                    sawText = true;
                    yield return delta.Text;
                    break;
                case ToolUseRequested when !notedToolUse:
                    // クライアントは共有 AiSettings.SystemPrompt（ツール操作を促す内容）を system に注入するため、
                    // モデルがツール呼び出しを返すことがある。解析では実行せず無視するが、空レポートを避けるため一度だけ通知する。
                    notedToolUse = true;
                    yield return "（モデルがツール呼び出しを返しました。解析では実行されません。テキストでの提案を待っています…）\n";
                    break;
                case AgentError err:
                    yield return $"\n\n⚠️ 解析エラー: {err.Message}";
                    yield break;
            }
        }

        if (!sawText && !notedToolUse)
            yield return "（提案を取得できませんでした。Ollama の設定や起動状態を確認してください。）";
    }

    private static string BuildPrompt(AdvisorInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("あなたはAIエージェント「Loomo」の動作ログを解析する改善アドバイザーです。");
        sb.AppendLine("以下の『固定システムプロンプト』『ツール一覧』『動作メトリクス』『失敗サンプル』をもとに、");
        sb.AppendLine("エージェントループ・ツールの使われ方・実装上の問題点を診断し、具体的な改善案を日本語で示してください。");
        sb.AppendLine("システムプロンプトはユーザー設定として編集できない固定値です。改訂版の全文は出力しないでください。");
        sb.AppendLine("これはメタ解析タスクです。ツールは一切呼び出さず、必ずテキスト（Markdown）だけで回答してください。");
        sb.AppendLine();
        sb.AppendLine("出力は次の Markdown 構成にしてください：");
        sb.AppendLine("## 分析 … メトリクスから読み取れる傾向・問題点");
        sb.AppendLine("## 推奨 … エージェントループ/ツール運用/実装の具体的な改善提案（箇条書き）");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("# 固定システムプロンプト");
        sb.AppendLine(input.CurrentSystemPrompt);
        sb.AppendLine();
        sb.AppendLine("# ツール一覧");
        foreach (var t in input.Tools)
            sb.AppendLine($"- {t.Name}: {t.Description}");
        sb.AppendLine();
        sb.AppendLine("# 動作メトリクス");
        sb.AppendLine(FormatMetrics(input.Single, input.Cross));

        if (input.FailureSamples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 失敗サンプル");
            foreach (var s in input.FailureSamples)
                sb.AppendLine($"- {s}");
        }

        return sb.ToString();
    }

    private static string FormatMetrics(SessionMetrics? single, CrossSessionMetrics? cross)
    {
        var sb = new StringBuilder();
        if (single is not null)
        {
            sb.AppendLine($"対象: 単一セッション ({single.SessionId})  provider={single.Provider ?? "?"}");
            sb.AppendLine($"ターン数={single.TurnCount} 平均反復={single.AvgIterations:0.0} 総ツール呼出={single.ToolCallCount}(失敗{single.ToolErrorCount})");
            sb.AppendLine($"安全ブロック={single.SafetyBlockCount} 承認={single.ApprovalCount}(許可{single.ApprovalApprovedCount}) 平均承認待ち={single.AvgApprovalWaitMs:0}ms");
            if (single.AvgTimeToFirstTokenMs is { } ttft) sb.AppendLine($"平均TTFT={ttft:0}ms");
            AppendToolStats(sb, single.ToolStats);
            if (single.Errors.Count > 0)
                sb.AppendLine("エラー: " + string.Join(" / ", single.Errors.Take(8).Select(e => $"[{e.Where}]{e.Message}")));
        }
        if (cross is not null)
        {
            sb.AppendLine($"対象: 横断 {cross.SessionCount}セッション / {cross.TurnCount}ターン 平均反復={cross.AvgIterations:0.0}");
            sb.AppendLine($"総ツール呼出={cross.ToolCallCount}(失敗{cross.ToolErrorCount}) 安全ブロック={cross.SafetyBlockCount} 承認={cross.ApprovalCount} 平均承認待ち={cross.AvgApprovalWaitMs:0}ms");
            if (cross.AvgTimeToFirstTokenMs is { } ttft) sb.AppendLine($"平均TTFT={ttft:0}ms");
            AppendToolStats(sb, cross.ToolStats);
            if (cross.ErrorsByWhere.Count > 0)
                sb.AppendLine("エラー発生箇所: " + string.Join(" / ", cross.ErrorsByWhere.Take(8).Select(e => $"{e.Key}×{e.Value}")));
        }
        return sb.ToString();
    }

    private static void AppendToolStats(StringBuilder sb, IReadOnlyList<ToolStat> stats)
    {
        foreach (var t in stats)
            sb.AppendLine($"  - {t.Name}: 呼出{t.Calls} 失敗{t.Errors}({t.ErrorRate:P0}) 平均{t.AvgDurationMs:0}ms");
    }

    /// <summary>
    /// トレースから改善材料となる失敗サンプルを抽出する：失敗ツール呼び出し（名前＋引数）と
    /// 記録されたエラー。長すぎる内容は切り詰める。
    /// </summary>
    public static IReadOnlyList<string> BuildFailureSamples(IReadOnlyList<TraceEvent> events, int max = 12)
    {
        var samples = new List<string>();

        // ai.tool_use の引数を toolUseId で引けるようにする。
        var argsByUseId = new Dictionary<string, string>();
        foreach (var ev in events.Where(e => e.Kind == TraceKinds.AiToolUse))
        {
            if (ev.Payload is not JsonElement p || p.ValueKind != JsonValueKind.Object) continue;
            var id = GetStr(p, "toolUseId");
            if (id is not null) argsByUseId[id] = GetStr(p, "argsJson") ?? "";
        }

        foreach (var ev in events)
        {
            if (samples.Count >= max) break;
            if (ev.Payload is not JsonElement p || p.ValueKind != JsonValueKind.Object) continue;

            if (ev.Kind == TraceKinds.ToolCompleted && (GetBool(p, "isError") ?? false))
            {
                var name = GetStr(p, "name") ?? "(unknown)";
                var useId = GetStr(p, "toolUseId");
                var args = useId is not null && argsByUseId.TryGetValue(useId, out var a) ? a : "";
                samples.Add(Trunc($"失敗ツール {name}({args})", 280));
            }
            else if (ev.Kind == TraceKinds.Error)
            {
                samples.Add(Trunc($"エラー[{GetStr(p, "where")}] {GetStr(p, "message")}", 280));
            }
        }
        return samples;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string? GetStr(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
