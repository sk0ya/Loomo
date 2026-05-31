using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentStudio.Core.Abstractions;
using AgentStudio.Core.Models;
using AgentStudio.Core.Tools;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Core.Agent;

/// <summary>
/// エージェントループ本体（UI非依存）。
/// user入力 → AI → tool_use → (承認) → tool実行 → 結果をAIへ → … → 最終テキスト。
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IAiClientFactory _aiFactory;
    private readonly ToolRegistry _tools;
    private readonly IApprovalService _approval;
    private readonly ILogger<AgentOrchestrator> _logger;

    private const int MaxIterations = 25;

    public AgentOrchestrator(
        IAiClientFactory aiFactory,
        ToolRegistry tools,
        IApprovalService approval,
        ILogger<AgentOrchestrator> logger)
    {
        _aiFactory = aiFactory;
        _tools = tools;
        _approval = approval;
        _logger = logger;
    }

    /// <summary>ユーザー入力を処理してイベントを流す。</summary>
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        Conversation conversation,
        string userInput,
        [EnumeratorCancellation] CancellationToken ct)
    {
        conversation.AddUser(userInput);
        var ai = _aiFactory.ResolveCurrent();
        var definitions = _tools.Definitions;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var assistant = new ChatMessage { Role = ChatRole.Assistant };
            var pendingToolUses = new List<ToolUse>();

            // --- AIストリームを消費 ---
            await foreach (var ev in ai.StreamAsync(conversation, definitions, ct))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        assistant.Text = (assistant.Text ?? string.Empty) + delta.Text;
                        yield return delta;
                        break;
                    case ToolUseRequested req:
                        assistant.ToolUses.Add(req.ToolUse);
                        pendingToolUses.Add(req.ToolUse);
                        yield return req;
                        break;
                    case AgentError err:
                        yield return err;
                        yield break;
                }
            }

            conversation.Messages.Add(assistant);

            // ツール呼び出しが無ければターン終了
            if (pendingToolUses.Count == 0)
            {
                yield return new TurnCompleted(assistant.Text);
                yield break;
            }

            // --- ツールを順に実行 ---
            var toolMessage = new ChatMessage { Role = ChatRole.Tool };
            var sink = new List<AgentEvent>();
            foreach (var use in pendingToolUses)
            {
                ct.ThrowIfCancellationRequested();
                sink.Clear();
                var result = await ExecuteToolAsync(use, sink, ct);
                foreach (var e in sink) yield return e;   // 実行中に貯めたイベントを流す
                toolMessage.ToolResults.Add(result);
            }

            conversation.Messages.Add(toolMessage);
            // ループ継続 → 結果を踏まえてAIが再応答
        }

        yield return new AgentError($"最大反復回数({MaxIterations})に達しました。");
    }

    private async Task<ToolResultMessage> ExecuteToolAsync(
        ToolUse use,
        List<AgentEvent> sink,
        CancellationToken ct)
    {
        if (!_tools.TryGet(use.Name, out var tool))
        {
            var msg = $"未知のツール: {use.Name}";
            _logger.LogWarning("{Message}", msg);
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
            return new ToolResultMessage(use.Id, $"引数JSONの解析失敗: {ex.Message}", IsError: true);
        }

        // 承認
        if (tool.RequiresApproval)
        {
            var summary = SafeDescribe(tool, args);
            sink.Add(new ApprovalRequested(tool.Name, summary));
            var approved = await _approval.RequestApprovalAsync(tool.Name, summary, ct);
            if (!approved)
                return new ToolResultMessage(use.Id, "ユーザーが実行を拒否しました。", IsError: true);
        }

        sink.Add(new ToolExecutionStarted(use));
        try
        {
            var result = await tool.ExecuteAsync(args, ct);
            var resultMessage = new ToolResultMessage(use.Id, result.Content, result.IsError);
            sink.Add(new ToolExecutionCompleted(use, resultMessage));
            return resultMessage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ツール実行エラー: {Tool}", tool.Name);
            var resultMessage = new ToolResultMessage(use.Id, $"ツール例外: {ex.Message}", IsError: true);
            sink.Add(new ToolExecutionCompleted(use, resultMessage));
            return resultMessage;
        }
    }

    private static string SafeDescribe(IAgentTool tool, JsonElement args)
    {
        try { return tool.DescribeInvocation(args); }
        catch { return tool.Name; }
    }
}
