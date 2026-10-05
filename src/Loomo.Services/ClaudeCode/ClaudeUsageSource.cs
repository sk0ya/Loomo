using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 表示に使う使用量の状態。<see cref="Usage"/> は最後に取れた値（取れていなければ null）、
/// <see cref="Problem"/> は直近の取得の結果、<see cref="RetryAt"/> は取得制限で待っている間の再開時刻。
/// </summary>
public sealed record ClaudeUsageSnapshot(
    ClaudeUsage? Usage, DateTimeOffset? FetchedAt, ClaudeUsageProblem Problem, DateTimeOffset? RetryAt);

/// <summary>
/// <see cref="ClaudeUsageClient"/> の前に置く、ファイル（<c>%APPDATA%/Loomo/claude-usage.json</c>）共有の窓口。
/// <c>api/oauth/usage</c> は同じトークンでの取得を 429 でかなり厳しく断るうえ、表示は「一度でも取れたら出る」なので、
/// 起動したのが断られている最中だと何も出なかった。そこで
/// <list type="bullet">
/// <item>最後に取れた値を保存し、起動直後から出す（取得の成否と表示を切り離す）</item>
/// <item>429 は間隔を倍々に延ばして待つ。待ちもファイルに置くので、他の Loomo も一緒に待つ</item>
/// <item>どれかの Loomo が直前に取りに行っていれば取りに行かない（何個起動しても取得は増えない）</item>
/// </list>
/// </summary>
public sealed class ClaudeUsageSource
{
    /// <summary>この間にどこかが取りに行っていれば、取りに行かずに保存した値を使う。定期取得（3分）より少し短くして、自分の番は取りこぼさない。</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2.5);

    /// <summary>429 の最初の待ち。以後は倍々で <see cref="MaxBackoff"/> まで。</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ClaudeUsageClient _client;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _now;

    public ClaudeUsageSource(ClaudeUsageClient client, string statePath, Func<DateTimeOffset>? now = null)
    {
        _client = client;
        _statePath = statePath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "claude-usage.json");

    /// <summary>保存してある状態（取りに行かない）。起動直後の表示用。</summary>
    public ClaudeUsageSnapshot LoadCached() => ToSnapshot(ReadState());

    /// <summary>
    /// 必要なら取りに行って、表示する状態を返す。<paramref name="force"/>（クリック）は「直前に取った」を無視するが、
    /// 429 の待ちは守る——叩けば待ちが延びるだけなので。
    /// </summary>
    public async Task<ClaudeUsageSnapshot> GetAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _now();
        var state = ReadState();
        if (state.RetryAt is { } retryAt && retryAt > now) return ToSnapshot(state);
        if (!force && state.LastAttemptAt is { } last && now - last < FreshFor && now >= last) return ToSnapshot(state);

        // 先に「取りに行った」と書いておき、同時に起動した他の Loomo が重ねて取りに行かないようにする。
        state = state with { LastAttemptAt = now };
        WriteState(state);

        var result = await _client.FetchAsync(cancellationToken).ConfigureAwait(false);
        state = Apply(state, result, _now());
        WriteState(state);
        return ToSnapshot(state);
    }

    /// <summary>取得の結果を状態に畳み込む。値は「未ログイン」のときだけ捨てる（失敗しても最後の値は出し続ける）。</summary>
    public static ClaudeUsageState Apply(ClaudeUsageState state, ClaudeUsageResult result, DateTimeOffset now)
    {
        switch (result.Problem)
        {
            case ClaudeUsageProblem.None when result.Usage is { } usage:
                return state with
                {
                    FiveHour = usage.FiveHour, SevenDay = usage.SevenDay, FetchedAt = now,
                    Problem = ClaudeUsageProblem.None, RetryAt = null, RateLimitCount = 0,
                };
            case ClaudeUsageProblem.NotSignedIn:
                return new ClaudeUsageState { LastAttemptAt = state.LastAttemptAt, Problem = ClaudeUsageProblem.NotSignedIn };
            case ClaudeUsageProblem.RateLimited:
                var count = state.RateLimitCount + 1;
                return state with
                {
                    Problem = ClaudeUsageProblem.RateLimited, RateLimitCount = count, RetryAt = now + Backoff(count),
                };
            default:
                return state with { Problem = result.Problem, RetryAt = null };
        }
    }

    /// <summary>n 回続けて 429 だったときの待ち（3分, 6分, 12分, 24分, 30分…）。</summary>
    public static TimeSpan Backoff(int count)
    {
        var minutes = InitialBackoff.TotalMinutes * Math.Pow(2, Math.Clamp(count - 1, 0, 10));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxBackoff.TotalMinutes));
    }

    private static ClaudeUsageSnapshot ToSnapshot(ClaudeUsageState state)
    {
        var usage = state.FiveHour is null && state.SevenDay is null ? null : new ClaudeUsage(state.FiveHour, state.SevenDay);
        return new(usage, usage is null ? null : state.FetchedAt, state.Problem,
            state.Problem == ClaudeUsageProblem.RateLimited ? state.RetryAt : null);
    }

    private ClaudeUsageState ReadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new ClaudeUsageState();
            using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ClaudeUsageState>(stream, JsonOptions) ?? new ClaudeUsageState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ClaudeUsageState();
        }
    }

    /// <summary>一時ファイルに書いて差し替える（他の Loomo が書きかけを読まないように）。書けなくても表示は続ける。</summary>
    private void WriteState(ClaudeUsageState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temp = $"{_statePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary><c>claude-usage.json</c> の中身。Loomo が何個起動していても、この1ファイルを共有する。</summary>
public sealed record ClaudeUsageState
{
    public ClaudeUsageWindow? FiveHour { get; init; }
    public ClaudeUsageWindow? SevenDay { get; init; }
    /// <summary>値が取れた時刻。</summary>
    public DateTimeOffset? FetchedAt { get; init; }
    /// <summary>どこかの Loomo が最後に取りに行った時刻（成否を問わない）。</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }
    public ClaudeUsageProblem Problem { get; init; }
    /// <summary>429 で待っている間の再開時刻。</summary>
    public DateTimeOffset? RetryAt { get; init; }
    /// <summary>続けて 429 だった回数（待ちの長さを決める）。</summary>
    public int RateLimitCount { get; init; }
}
