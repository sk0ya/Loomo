using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Microsoft.Extensions.Logging;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// エージェントループ本体（UI非依存）。
/// user入力 → AI → tool_use → (承認) → tool実行 → 結果をAIへ → … → 最終テキスト。
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IAiClientFactory _aiFactory;
    private readonly ToolRegistry _tools;
    private readonly IApprovalService _approval;
    private readonly ISafetyPolicy _safety;
    private readonly IContextWindowPolicy _context;
    private readonly ITraceSink _trace;
    private readonly ILogger<AgentOrchestrator> _logger;

    private const int MaxIterations = 25;

    public AgentOrchestrator(
        IAiClientFactory aiFactory,
        ToolRegistry tools,
        IApprovalService approval,
        ISafetyPolicy safety,
        IContextWindowPolicy context,
        ILogger<AgentOrchestrator> logger,
        ITraceSink? trace = null)
    {
        _aiFactory = aiFactory;
        _tools = tools;
        _approval = approval;
        _safety = safety;
        _context = context;
        _logger = logger;
        _trace = trace ?? NullTraceSink.Instance;
    }

    /// <summary>ユーザー入力を処理してイベントを流す。</summary>
    /// <param name="sessionId">トレース記録用のセッションID（§20）。null/空ならトレースは "unknown" にまとまる。</param>
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        Conversation conversation,
        string userInput,
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        AgentProfile? profile = null)
    {
        sessionId ??= "unknown";
        var requestedProfile = profile ?? AgentProfiles.Root;
        var useResidentPipeline = ReferenceEquals(requestedProfile, AgentProfiles.Root)
                                  || requestedProfile.Id == AgentProfiles.Root.Id;
        var isNewSession = conversation.Messages.Count == 0;

        conversation.AddUser(userInput);
        var ai = _aiFactory.ResolveCurrent();
        var definitions = _tools.Definitions;

        var turnId = Guid.NewGuid().ToString("N");
        var provider = ai.Provider.ToString();
        var turnClock = Stopwatch.StartNew();

        if (isNewSession)
            _trace.Record(sessionId, null, TraceKinds.SessionStarted, new { provider, agentId = requestedProfile.Id });
        _trace.Record(sessionId, turnId, TraceKinds.TurnStarted, new
        {
            userInput,
            provider,
            agentId = requestedProfile.Id,
            pipeline = useResidentPipeline ? "resident" : "single-profile",
        });

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var assistant = new ChatMessage { Role = ChatRole.Assistant };
            var pendingToolUses = new List<ToolUse>();
            var sawModelOutput = false;
            var activeProfile = useResidentPipeline
                ? SelectResidentProfile(conversation)
                : requestedProfile;
            var activeDefinitions = activeProfile.Id == AgentProfiles.ResultJudge.Id
                                    || activeProfile.Id == AgentProfiles.ChatUnderstanding.Id
                ? Array.Empty<ToolDefinition>()
                : definitions;

            // コンテキスト超過を防ぐため、送信用に履歴をトリム（元会話は保持）。
            var outgoing = _context.Fit(conversation, activeProfile);

            // --- AIストリームを消費 ---
            AgentError? streamError = null;
            _trace.Record(sessionId, turnId, TraceKinds.AiMessage, new
            {
                agentId = activeProfile.Id,
                stage = activeProfile.DisplayName,
                started = true,
                iteration = iteration + 1,
                toolCount = activeDefinitions.Count,
            });
            await foreach (var ev in ai.StreamAsync(outgoing, activeDefinitions, ct, activeProfile))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        sawModelOutput = true;
                        assistant.Text = (assistant.Text ?? string.Empty) + delta.Text;
                        yield return delta;
                        break;
                    case ThinkingDelta thinking:
                        sawModelOutput = true;
                        // 思考は応答本文ではないため履歴へは積まず、UI表示用に通すだけ。
                        yield return thinking;
                        break;
                    case ToolUseRequested req:
                        sawModelOutput = true;
                        assistant.ToolUses.Add(req.ToolUse);
                        pendingToolUses.Add(req.ToolUse);
                        yield return req;
                        break;
                    case AgentError err:
                        streamError = err;
                        break;
                }
                if (streamError is not null) break;
            }

            if (streamError is not null)
            {
                _trace.Record(sessionId, turnId, TraceKinds.Error,
                    new { message = streamError.Message, where = "ai.stream" });
                yield return streamError;
                yield break;
            }

            if (!sawModelOutput)
            {
                var emptyError = new AgentError("AI から応答が返りませんでした。ローカルLLMの起動状態とモデル名を確認してください。");
                _trace.Record(sessionId, turnId, TraceKinds.Error,
                    new { message = emptyError.Message, where = "ai.empty_response" });
                yield return emptyError;
                yield break;
            }

            // ストリーム終了時にアシスタント本文をまとめて記録（TextDelta を合算した全文）。
            if (!string.IsNullOrEmpty(assistant.Text))
                _trace.Record(sessionId, turnId, TraceKinds.AiMessage, new
                {
                    fullText = assistant.Text,
                    agentId = activeProfile.Id,
                    stage = activeProfile.DisplayName,
                });
            foreach (var use in pendingToolUses)
                _trace.Record(sessionId, turnId, TraceKinds.AiToolUse,
                    new
                    {
                        toolUseId = use.Id,
                        name = use.Name,
                        argsJson = use.ArgumentsJson,
                        agentId = activeProfile.Id,
                        stage = activeProfile.DisplayName,
                    });

            // 本文もツール呼び出しも無い空応答は履歴に積まない（APIエラー要因になり得る）。
            if (!string.IsNullOrEmpty(assistant.Text) || pendingToolUses.Count > 0)
                conversation.Messages.Add(assistant);

            // ツール呼び出しが無ければターン終了
            if (pendingToolUses.Count == 0)
            {
                _trace.Record(sessionId, turnId, TraceKinds.TurnCompleted,
                    new { finalText = assistant.Text, iterations = iteration + 1, durationMs = turnClock.ElapsedMilliseconds });
                yield return new TurnCompleted(assistant.Text);
                yield break;
            }

            // --- ツールを順に実行 ---
            var toolMessage = new ChatMessage { Role = ChatRole.Tool };
            foreach (var use in pendingToolUses)
            {
                ct.ThrowIfCancellationRequested();

                // 実行中のイベント（承認待ち・実行中…）を貯め込まず即時に流す。Channel で実行タスクと
                // 並行に読み出すことで、承認待ちや長い実行の状態がリアルタイムにUIへ届く。
                var channel = Channel.CreateUnbounded<AgentEvent>();
                var execTask = ExecuteToolWithEventsAsync(use, sessionId, turnId, channel.Writer, ct);
                // ct は渡さない：writer は finally で必ず Complete されるので読み出しは自然に終わり、
                // キャンセルは execTask の await で観測される（未観測例外を防ぐ）。
                await foreach (var e in channel.Reader.ReadAllAsync())
                    yield return e;
                toolMessage.ToolResults.Add(await execTask);
            }

            conversation.Messages.Add(toolMessage);
            // ループ継続 → 結果を踏まえてAIが再応答
        }

        _trace.Record(sessionId, turnId, TraceKinds.Error,
            new { message = $"最大反復回数({MaxIterations})に達しました。", where = "iteration.limit" });
        yield return new AgentError($"最大反復回数({MaxIterations})に達しました。");
    }

    private static AgentProfile SelectResidentProfile(Conversation conversation) => conversation.Messages.Count > 0 && conversation.Messages[^1].Role == ChatRole.Tool
        ? AgentProfiles.ResultJudge
        : AgentProfiles.ChatUnderstanding;

    private static bool ShouldContinueResidentPipeline(string? text)
        => text?.TrimStart().StartsWith("[CONTINUE]", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary><see cref="ExecuteToolAsync"/> を実行し、終了時に必ずイベントチャネルを閉じる。
    /// これにより呼び出し側の読み出しループが確実に終了する。</summary>
    private async Task<ToolResultMessage> ExecuteToolWithEventsAsync(
        ToolUse use,
        string sessionId,
        string turnId,
        ChannelWriter<AgentEvent> events,
        CancellationToken ct)
    {
        try { return await ExecuteToolAsync(use, sessionId, turnId, events, ct); }
        finally { events.Complete(); }
    }

    private async Task<ToolResultMessage> ExecuteToolAsync(
        ToolUse use,
        string sessionId,
        string turnId,
        ChannelWriter<AgentEvent> events,
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

        events.TryWrite(new ToolExecutionStarted(use));
        _trace.Record(sessionId, turnId, TraceKinds.ToolStarted, new { toolUseId = use.Id, name = tool.Name });
        var toolClock = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(args, ct);
            var resultMessage = new ToolResultMessage(use.Id, result.Content, result.IsError);
            _trace.Record(sessionId, turnId, TraceKinds.ToolCompleted, new
            {
                toolUseId = use.Id,
                name = tool.Name,
                isError = result.IsError,
                durationMs = toolClock.ElapsedMilliseconds,
                contentLen = result.Content?.Length ?? 0,
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
}
