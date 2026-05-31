using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai.Http;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エージェントループの堅牢化（コンテキスト長トリム / HTTPリトライ）の検証。
/// </summary>
public class AgentLoopTests
{
    // ===== TokenEstimator =====

    [Fact]
    public void TokenEstimator_is_monotonic_with_length()
    {
        var shortMsg = new ChatMessage { Role = ChatRole.User, Text = "hi" };
        var longMsg = new ChatMessage { Role = ChatRole.User, Text = new string('x', 4000) };
        Assert.True(TokenEstimator.EstimateMessage(longMsg) > TokenEstimator.EstimateMessage(shortMsg));
    }

    // ===== ConversationTrimmer =====

    private static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };

    private static ChatMessage Assistant(string text, params ToolUse[] uses)
    {
        var m = new ChatMessage { Role = ChatRole.Assistant, Text = text };
        m.ToolUses.AddRange(uses);
        return m;
    }

    private static ChatMessage Tool(params ToolResultMessage[] results)
    {
        var m = new ChatMessage { Role = ChatRole.Tool };
        m.ToolResults.AddRange(results);
        return m;
    }

    [Fact]
    public void Trim_returns_original_when_within_budget()
    {
        var msgs = new List<ChatMessage> { User("a"), Assistant("b") };
        var result = ConversationTrimmer.Trim(msgs, budgetTokens: 1000);
        Assert.Same(msgs, result);
    }

    [Fact]
    public void Trim_returns_original_when_budget_is_non_positive()
    {
        var msgs = new List<ChatMessage> { User(new string('x', 10000)) };
        Assert.Same(msgs, ConversationTrimmer.Trim(msgs, 0));
    }

    [Fact]
    public void Trim_drops_oldest_and_keeps_recent_within_budget()
    {
        var big = new string('x', 4000); // ≒1000トークン/メッセージ
        var msgs = new List<ChatMessage>
        {
            User(big),       // 0
            Assistant(big),  // 1
            User(big),       // 2
            Assistant(big),  // 3
            User("最新の質問"), // 4
        };

        var result = ConversationTrimmer.Trim(msgs, budgetTokens: 1200);

        Assert.True(result.Count < msgs.Count);
        Assert.Same(msgs[^1], result[^1]); // 最新メッセージは必ず残る
        Assert.Equal(ChatRole.User, result[0].Role); // 先頭は必ず user
    }

    [Fact]
    public void Trim_never_starts_with_orphan_tool_result()
    {
        // assistant(tool_use) → tool(result) のペアの「途中」で切れても、
        // 先頭が tool_result にならない（user まで前方に詰める）こと。
        var big = new string('x', 8000);
        var use = new ToolUse("id1", "run_command", "{}");
        var msgs = new List<ChatMessage>
        {
            User("古い"),                                            // 0
            Assistant(big, use),                                     // 1 (大きい assistant)
            Tool(new ToolResultMessage("id1", "result", false)),     // 2
            User("最新"),                                            // 3
        };

        var result = ConversationTrimmer.Trim(msgs, budgetTokens: 50);

        Assert.NotEqual(ChatRole.Tool, result[0].Role);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Same(msgs[^1], result[^1]);
    }

    // ===== HttpRetry: 純粋関数 =====

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(408, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void IsTransient_classifies_status_codes(int code, bool expected)
        => Assert.Equal(expected, HttpRetry.IsTransient((HttpStatusCode)code));

    [Fact]
    public void BackoffDelay_grows_exponentially_and_caps()
    {
        var opts = new RetryOptions(MaxAttempts: 10, BaseDelay: TimeSpan.FromMilliseconds(500), MaxDelay: TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromMilliseconds(500), HttpRetry.BackoffDelay(1, opts));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), HttpRetry.BackoffDelay(2, opts));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), HttpRetry.BackoffDelay(3, opts));
        Assert.Equal(TimeSpan.FromSeconds(20), HttpRetry.BackoffDelay(20, opts)); // 上限で頭打ち
    }

    // ===== HttpRetry: 送信ループ（遅延はノーオペで差し替え） =====

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _codes;
        public int Calls { get; private set; }

        public SequenceHandler(params HttpStatusCode[] codes) => _codes = new Queue<HttpStatusCode>(codes);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    [Fact]
    public async Task SendAsync_retries_transient_then_succeeds()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        using var resp = await HttpRetry.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/"),
            CancellationToken.None,
            RetryOptions.Default,
            delay: (_, _) => Task.CompletedTask); // 待たない

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SendAsync_does_not_retry_permanent_error()
    {
        var handler = new SequenceHandler(HttpStatusCode.BadRequest, HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        using var resp = await HttpRetry.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/"),
            CancellationToken.None,
            RetryOptions.Default,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(1, handler.Calls); // 400 は再試行しない
    }

    private sealed class ThrowOnceHandler : HttpMessageHandler
    {
        private readonly Exception _toThrow;
        public int Calls { get; private set; }

        public ThrowOnceHandler(Exception toThrow) => _toThrow = toThrow;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) throw _toThrow;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SendAsync_retries_request_timeout_then_succeeds()
    {
        // HttpClient.Timeout 由来のタイムアウトは TaskCanceledException(OperationCanceledException) として飛ぶ。
        // ユーザーキャンセル(ct)ではないので一時障害として再試行されること。
        var handler = new ThrowOnceHandler(new TaskCanceledException("timeout"));
        using var http = new HttpClient(handler);

        using var resp = await HttpRetry.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/"),
            CancellationToken.None,
            RetryOptions.Default,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SendAsync_does_not_retry_user_cancellation()
    {
        // ct がキャンセル済みなら（=ユーザー中断）再試行せず OperationCanceledException を伝播する。
        var handler = new ThrowOnceHandler(new TaskCanceledException("cancelled"));
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpRetry.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/"),
            cts.Token,
            RetryOptions.Default,
            delay: (_, _) => Task.CompletedTask));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SendAsync_gives_up_after_max_attempts()
    {
        var handler = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var opts = new RetryOptions(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1), MaxDelay: TimeSpan.FromMilliseconds(1));

        using var resp = await HttpRetry.SendAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/"),
            CancellationToken.None,
            opts,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(3, handler.Calls); // MaxAttempts で打ち切り
    }
}
