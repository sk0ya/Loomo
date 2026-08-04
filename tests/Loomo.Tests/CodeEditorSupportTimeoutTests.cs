using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// LSP 要求の上限時間。言語サーバーが黙ったときに描画が永久に完了しない
/// （＝ペインが古い内容のまま固まる）のを防ぐ仕組みのテスト。
/// </summary>
public class CodeEditorSupportTimeoutTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task Unanswered_request_gives_up_and_reports_timeout()
    {
        var never = new TaskCompletionSource<string>();   // 応答を返さないサーバー

        var (value, timedOut) = await CodeEditorSupportAnalysis.WithLimitAsync(
            never.Task, Limit, CancellationToken.None, "test");

        Assert.True(timedOut);
        Assert.Null(value);
    }

    [Fact]
    public async Task Answered_request_passes_the_value_through()
    {
        var (value, timedOut) = await CodeEditorSupportAnalysis.WithLimitAsync(
            Task.FromResult("ok"), Limit, CancellationToken.None, "test");

        Assert.False(timedOut);
        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task Cancellation_is_reported_as_cancellation_not_as_timeout()
    {
        // 追い越された描画は「期限切れ」ではない。更新ループが次の周回でやり直せるよう、
        // OperationCanceledException として伝わること。
        var never = new TaskCompletionSource<string>();
        using var cts = new CancellationTokenSource();

        var pending = CodeEditorSupportAnalysis.WithLimitAsync(
            never.Task, TimeSpan.FromMinutes(1), cts.Token, "test");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Slow_but_answering_request_still_wins_within_the_limit()
    {
        var slow = Task.Run(async () => { await Task.Delay(20); return "late"; });

        var (value, timedOut) = await CodeEditorSupportAnalysis.WithLimitAsync(
            slow, TimeSpan.FromSeconds(5), CancellationToken.None, "test");

        Assert.False(timedOut);
        Assert.Equal("late", value);
    }
}
