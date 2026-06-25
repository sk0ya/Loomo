using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Microsoft.Extensions.Logging;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>1ツールの実行を担う（UI非依存）：ツールの解決・引数正規化・冗長上書きガード・安全評価・承認・
/// 実行・ジャーナル記録と、実行中イベント（承認待ち／実行中／完了）のチャネル送出。ループ本体は
/// <see cref="AgentOrchestrator"/>。</summary>
internal sealed class ToolExecutor
{
    private readonly ToolRegistry _tools;
    private readonly IApprovalService _approval;
    private readonly ISafetyPolicy _safety;
    private readonly ITraceSink _trace;
    private readonly IFileChangeJournal? _journal;
    private readonly ILogger _logger;

    public ToolExecutor(
        ToolRegistry tools,
        IApprovalService approval,
        ISafetyPolicy safety,
        ITraceSink trace,
        IFileChangeJournal? journal,
        ILogger logger)
    {
        _tools = tools;
        _approval = approval;
        _safety = safety;
        _trace = trace;
        _journal = journal;
        _logger = logger;
    }

    /// <summary><see cref="ExecuteToolAsync"/> を実行し、終了時に必ずイベントチャネルを閉じる。
    /// これにより呼び出し側の読み出しループが確実に終了する。</summary>
    public async Task<ToolResultMessage> ExecuteToolWithEventsAsync(
        ToolUse use,
        string sessionId,
        string turnId,
        ChannelWriter<AgentEvent> events,
        HashSet<string> editedPaths,
        CancellationToken ct)
    {
        try { return await ExecuteToolAsync(use, sessionId, turnId, events, editedPaths, ct); }
        finally { events.Complete(); }
    }

    private async Task<ToolResultMessage> ExecuteToolAsync(
        ToolUse use,
        string sessionId,
        string turnId,
        ChannelWriter<AgentEvent> events,
        HashSet<string> editedPaths,
        CancellationToken ct)
    {
        if (!_tools.TryGet(use.Name, out var tool))
        {
            var msg = $"未知のツール: {use.Name}";
            _logger.LogWarning("{Message}", msg);
            _trace.Record(sessionId, turnId, TraceKinds.Error, new { message = msg, where = "tool.resolve" });
            return new ToolResultMessage(use.Id, msg, IsError: true);
        }

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(use.ArgumentsJson) ? "{}" : use.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _trace.Record(sessionId, turnId, TraceKinds.Error, new { message = ex.Message, where = "tool.args" });
            return new ToolResultMessage(use.Id, $"引数JSONの解析失敗: {ex.Message}", IsError: true);
        }

        // キー揺れ等を canonical へ寄せる。安全評価・要約・実行が同じ正規化済み引数を見るよう、ここで一度だけ適用する。
        try { args = tool.NormalizeArguments(args); }
        catch (Exception ex)
        {
            _trace.Record(sessionId, turnId, TraceKinds.Error, new { message = ex.Message, where = "tool.normalize" });
            // 正規化に失敗しても元の引数のまま続行（安全評価・実行は通常どおり行う）。
        }

        // 冗長な破壊的上書きガード：同一ターンで edit_file が対象限定編集したファイルを、後続の write_file が
        // 全文上書きしようとしたら、実行せず差し戻す（直前の正しい編集を丸ごと破壊しない）。edit_file 自身の
        // 対象限定変更・追記は対象外（多段の絞り込み編集は正当）。write_file→write_file（自分で書いた全文の
        // 書き直し）も破壊ではないので対象外。pwsh など IFileMutationTool 非実装のツールも対象外。
        if (tool is IFileMutationTool mutation && mutation.FullyOverwritesTarget)
        {
            var target = SafeResolveTarget(mutation, args);
            if (target is not null && editedPaths.Contains(target))
            {
                var msg = "このターンで edit_file が編集したファイルを write_file で全文上書きしようとしました（"
                          + target + "）。直前の編集が破棄されるためブロックしました。変更は完了しています。"
                          + "やり直しは不要なので、ツールを呼ばず日本語で結果を報告してください。"
                          + "さらに修正が必要な場合のみ、edit_file で対象箇所だけを変更してください。";
                _logger.LogWarning("冗長な破壊的上書きをブロック: {Path}", target);
                _trace.Record(sessionId, turnId, TraceKinds.Error,
                    new { message = msg, where = "tool.redundant_overwrite", path = target });
                var blocked = new ToolResultMessage(use.Id, msg, IsError: true);
                events.TryWrite(new ToolExecutionStarted(use));
                events.TryWrite(new ToolExecutionCompleted(use, blocked));
                return blocked;
            }
        }

        // 安全ポリシー：危険コマンドのブロックリストに一致したら実行せず差し戻す
        var decision = _safety.Evaluate(tool.Name, args);
        _trace.Record(sessionId, turnId, TraceKinds.SafetyEvaluated,
            new { tool = tool.Name, blocked = decision.Blocked, reason = decision.Reason });
        if (decision.Blocked)
        {
            _logger.LogWarning("安全ポリシーによりブロック: {Tool} — {Reason}", tool.Name, decision.Reason);
            var blocked = new ToolResultMessage(use.Id, decision.Reason!, IsError: true);
            events.TryWrite(new ToolExecutionStarted(use));
            events.TryWrite(new ToolExecutionCompleted(use, blocked));
            return blocked;
        }

        // 承認（自動承認モードが有効ならスキップ）
        if (tool.RequiresApproval && !_safety.AutoApprove)
        {
            var summary = SafeDescribe(tool, args);
            events.TryWrite(new ApprovalRequested(tool.Name, summary));
            _trace.Record(sessionId, turnId, TraceKinds.ApprovalRequested, new { tool = tool.Name, summary });
            var approvalClock = Stopwatch.StartNew();
            var approved = await _approval.RequestApprovalAsync(tool.Name, summary, ct);
            _trace.Record(sessionId, turnId, TraceKinds.ApprovalResolved,
                new { tool = tool.Name, approved, waitMs = approvalClock.ElapsedMilliseconds });
            if (!approved)
                return new ToolResultMessage(use.Id, "ユーザーが実行を拒否しました。", IsError: true);
        }

        // Diff セッション用：ファイル変更ツールは実行前の全文を控えておき、成功後に前後ペアで記録する。
        string? journalPath = null;
        var journalExistedBefore = false;
        string? journalBefore = null;
        if (_journal is not null && tool is IFileMutationTool journalTool)
        {
            journalPath = SafeResolveTarget(journalTool, args);
            if (journalPath is not null)
                (journalExistedBefore, journalBefore) = FileChangeJournal.SafeReadFile(journalPath);
        }

        events.TryWrite(new ToolExecutionStarted(use));
        _trace.Record(sessionId, turnId, TraceKinds.ToolStarted, new { toolUseId = use.Id, name = tool.Name });
        var toolClock = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(args, ct);
            var content = NormalizeToolResultContent(tool.Name, result);
            var resultMessage = new ToolResultMessage(use.Id, content, result.IsError);
            // 対象限定編集（edit_file）に成功したファイルだけ記録：以降の反復で同じファイルへの破壊的な
            // 全文上書き（write_file）をガードする。write_file 自身の成功は記録しない（全文の書き直しは破壊でない）。
            if (!result.IsError && tool is IFileMutationTool fileTool && !fileTool.FullyOverwritesTarget)
            {
                var target = SafeResolveTarget(fileTool, args);
                if (target is not null) editedPaths.Add(target);
            }
            // ファイル変更に成功したら実行後の全文を読み、前後ペアをジャーナルへ記録する（Diff セッション用）。
            if (!result.IsError && journalPath is not null && _journal is not null)
            {
                var (_, journalAfter) = FileChangeJournal.SafeReadFile(journalPath);
                if (!journalExistedBefore || !string.Equals(journalBefore, journalAfter, StringComparison.Ordinal))
                    _journal.Record(new FileChangeRecord(
                        DateTimeOffset.Now, sessionId, turnId, tool.Name, journalPath,
                        IsNew: !journalExistedBefore, journalBefore, journalAfter));
            }
            _trace.Record(sessionId, turnId, TraceKinds.ToolCompleted, new
            {
                toolUseId = use.Id,
                name = tool.Name,
                isError = result.IsError,
                durationMs = toolClock.ElapsedMilliseconds,
                contentLen = content.Length,
                originalContentLen = result.Content?.Length ?? 0,
            });
            events.TryWrite(new ToolExecutionCompleted(use, resultMessage));
            return resultMessage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ツール実行エラー: {Tool}", tool.Name);
            _trace.Record(sessionId, turnId, TraceKinds.ToolCompleted, new
            {
                toolUseId = use.Id,
                name = tool.Name,
                isError = true,
                durationMs = toolClock.ElapsedMilliseconds,
                error = ex.Message,
            });
            var resultMessage = new ToolResultMessage(use.Id, $"ツール例外: {ex.Message}", IsError: true);
            events.TryWrite(new ToolExecutionCompleted(use, resultMessage));
            return resultMessage;
        }
    }

    private static string SafeDescribe(IAgentTool tool, JsonElement args)
    {
        try { return tool.DescribeInvocation(args); }
        catch { return tool.Name; }
    }

    /// <summary>ファイル変更ツールの対象パスを安全に解決（例外は null 扱い）。冗長上書きガードの比較キー用。</summary>
    private static string? SafeResolveTarget(IFileMutationTool tool, JsonElement args)
    {
        try { return tool.ResolveTargetPath(args); }
        catch { return null; }
    }

    private static string NormalizeToolResultContent(string toolName, ToolResult result)
    {
        if (!string.IsNullOrEmpty(result.Content))
            return result.Content;

        var state = result.IsError ? "failed" : "completed";
        return $"tool {state}: {toolName} (no output)";
    }
}
