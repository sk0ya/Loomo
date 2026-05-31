using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Ai.Http;

/// <summary>リトライ設定。</summary>
public sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    /// <summary>既定: 最大4回、初回500ms、指数バックオフ上限20秒。</summary>
    public static readonly RetryOptions Default =
        new(MaxAttempts: 4, BaseDelay: TimeSpan.FromMilliseconds(500), MaxDelay: TimeSpan.FromSeconds(20));
}

/// <summary>
/// 一時的な障害（429/5xx・ネットワーク例外）に対する指数バックオフ付きリトライ。
/// エージェントは長時間走るため、瞬間的なレート制限やサーバ過負荷で会話を落とさないようにする。
/// 判定（<see cref="IsTransient"/>）と遅延計算（<see cref="BackoffDelay"/>）は純粋関数で単体テスト可能。
/// </summary>
public static class HttpRetry
{
    /// <summary>再試行する価値のある（一時的な）ステータスコードか。</summary>
    public static bool IsTransient(HttpStatusCode code) => (int)code switch
    {
        408 => true,                 // Request Timeout
        429 => true,                 // Too Many Requests
        >= 500 and <= 599 => true,   // サーバ側一時障害（502/503/504/529 等）
        _ => false
    };

    /// <summary>試行回数に対する指数バックオフ遅延（ジッタ無し・決定的）。attempt は1始まり。</summary>
    public static TimeSpan BackoffDelay(int attempt, RetryOptions options)
    {
        if (attempt < 1) attempt = 1;
        var factor = Math.Pow(2, attempt - 1);
        var ms = options.BaseDelay.TotalMilliseconds * factor;
        var capped = Math.Min(ms, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }

    /// <summary>
    /// <paramref name="requestFactory"/> で毎回新しい <see cref="HttpRequestMessage"/> を作って送信し、
    /// 一時障害なら待機して再試行する。成功/恒久エラー/試行打ち切り時の応答を返す（呼び出し側が dispose する）。
    /// <paramref name="delay"/> はテスト用に差し替え可能（既定は <see cref="Task.Delay(TimeSpan, CancellationToken)"/>）。
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct,
        RetryOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var opts = options ?? RetryOptions.Default;
        delay ??= static (d, c) => Task.Delay(d, c);

        for (var attempt = 1; ; attempt++)
        {
            var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < opts.MaxAttempts)
            {
                await delay(BackoffDelay(attempt, opts), ct);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // ユーザーキャンセル(ct)ではなく HttpClient.Timeout 由来のタイムアウト。一時障害として扱う。
                if (attempt >= opts.MaxAttempts)
                    throw new TimeoutException($"リクエストがタイムアウトしました（{opts.MaxAttempts}回試行）。");
                await delay(BackoffDelay(attempt, opts), ct);
                continue;
            }
            finally
            {
                // 送信完了後（成功・例外いずれも）リクエストは破棄してよい（応答ストリームは独立）。
                request.Dispose();
            }

            if (response.IsSuccessStatusCode
                || !IsTransient(response.StatusCode)
                || attempt >= opts.MaxAttempts)
            {
                return response;
            }

            // 一時障害: Retry-After を尊重しつつバックオフ。
            var retryAfter = GetRetryAfter(response);
            response.Dispose();
            var wait = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : BackoffDelay(attempt, opts);
            await delay(wait, ct);
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }
}
