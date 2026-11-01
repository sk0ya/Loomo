using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Git;

namespace sk0ya.Loomo.Services;

/// <summary>
/// git・ssh の資格情報の問いを部屋の中で受ける（§24.101）。<c>sk0ya.Loomo.AskPass.exe</c> からの問いを名前付きパイプで受け、
/// <see cref="IGitCredentialPrompter"/>（App の入力欄）に聞いて答えを返す。
/// <para>パイプ名は Loomo のプロセスごとに使い捨て（PID＋乱数）で、今のユーザーにしか開けない
/// （<see cref="PipeOptions.CurrentUserOnly"/>）。問うた git が答えを待たずに居なくなったら（時間切れ・中止）、
/// exe が切れるので入力欄を閉じる。</para>
/// <para>「次から聞かれないようにする」が選ばれたら、答えを git の起動ごとの記録に一時的に預かり、その git が終わったとき
/// （<see cref="Complete"/>）に <see cref="IGitCredentialRegistrar"/> へ渡して捨てる——HTTPS は認証が通ったときだけ
/// （間違ったパスワードを覚えさせない）。SSH の合言葉は ssh-add 自身が確かめるので、git の成否は問わない。</para>
/// <para>exe が見つからなければ（ビルドの途中など）何も足さない——そのときは今まで通り、対話が要る操作はその場で失敗する。</para>
/// </summary>
public sealed class GitAskPassServer : IDisposable
{
    private readonly IGitCredentialPrompter _prompter;
    private readonly IGitCredentialRegistrar? _registrar;
    private readonly string _helperPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _started;
    /// <summary>git の起動ごとの記録（人が答えていた時間・覚えさせる答え）。</summary>
    private readonly ConcurrentDictionary<string, InvocationState> _invocations = new();
    /// <summary>人に聞かずに一度だけ返す答え（覚えさせるときの ssh-add に、入力済みの合言葉を渡す）。使ったら null。</summary>
    private readonly ConcurrentDictionary<string, string?> _answering = new();

    public GitAskPassServer(IGitCredentialPrompter prompter, string helperPath, string? pipeName = null,
        IGitCredentialRegistrar? registrar = null)
    {
        _prompter = prompter;
        _registrar = registrar;
        _helperPath = helperPath;
        PipeName = pipeName ?? $"loomo-askpass-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    public string PipeName { get; }

    /// <summary>git に渡す環境変数。<paramref name="context"/> は入力欄に出す「どの操作か」（例 <c>git push</c>）、
    /// <paramref name="invocation"/> は git の起動ごとの印（<see cref="TimeSpentAsking"/>・<see cref="Complete"/> で引く）。
    /// exe が無ければ null（何も足さない）。初めて呼ばれたときに受け口を開く。</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentFor(string context, string? invocation = null)
    {
        if (!File.Exists(_helperPath))
            return null;
        EnsureStarted();
        return new Dictionary<string, string>
        {
            ["GIT_ASKPASS"] = _helperPath,
            ["SSH_ASKPASS"] = _helperPath,
            // 端末が無くても、表示（DISPLAY）が無くても askpass を使わせる（OpenSSH 8.4+）。
            ["SSH_ASKPASS_REQUIRE"] = "force",
            [GitAskPass.PipeVariable] = PipeName,
            [GitAskPass.ContextVariable] = context,
            [GitAskPass.InvocationVariable] = invocation ?? "",
        };
    }

    /// <summary>人に聞かずに <paramref name="answer"/> を一度だけ返す環境（2 度目の問いは断る＝合言葉が違えば失敗で終わる）。</summary>
    public (IReadOnlyDictionary<string, string>? Environment, string Invocation) EnvironmentAnswering(string context, string answer)
    {
        var invocation = Guid.NewGuid().ToString("N");
        _answering[invocation] = answer;
        return (EnvironmentFor(context, invocation), invocation);
    }

    /// <summary>その git の起動が、これまでに人へ聞いていた時間（いま聞いている最中ならその分も）。</summary>
    public TimeSpan TimeSpentAsking(string invocation)
        => _invocations.TryGetValue(invocation, out var state) ? state.Time.Total : TimeSpan.Zero;

    /// <summary>
    /// git が終わった。記録を捨て、覚えさせる答えがあれば覚えさせる（裏で。git の結果は待たせない）。
    /// <paramref name="authenticated"/> は認証が通ったか（成功したか、認証以外の理由で止まったか）。
    /// </summary>
    public void Complete(string invocation, bool authenticated)
    {
        _answering.TryRemove(invocation, out _);
        if (!_invocations.TryRemove(invocation, out var state) || state.Remember is not { } pending || _registrar is null)
            return;
        if (pending.Remember is GitCredentialRemember.Https https && !authenticated)
        {
            _prompter.Report($"{https.Origin} の認証が通らなかったので、資格情報を覚えさせませんでした。", success: false);
            return;
        }
        _ = Task.Run(async () =>
        {
            var answering = pending.Remember is GitCredentialRemember.SshKey
                ? EnvironmentAnswering("ssh-add", pending.Secret)
                : (Environment: null, Invocation: "");
            try
            {
                var result = await _registrar.RememberAsync(pending.Remember, pending.UserName, pending.Secret,
                    answering.Environment).ConfigureAwait(false);
                _prompter.Report(result.Message, result.Success);
            }
            catch (Exception ex)
            {
                _prompter.Report($"覚えさせられませんでした：{ex.Message}", success: false);
            }
            finally
            {
                _answering.TryRemove(answering.Invocation, out _);
            }
        });
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _ = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // 非同期ハンドル必須（同期だと読み待ちが書きを塞ぐ。memory: named-pipe-sync-deadlock）。
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[askpass] 受け口のエラー: {ex.Message}");
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        await using var connection = pipe;
        using var asker = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        try
        {
            using (var first = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                first.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await GitAskPass.ReadLineAsync(pipe, first.Token).ConfigureAwait(false);
                if (line is null || JsonSerializer.Deserialize<GitAskPass.Request>(line, GitAskPass.Json) is not { } request)
                    return;

                var reply = await AnswerAsync(pipe, request, asker).ConfigureAwait(false);
                await GitAskPass.WriteLineAsync(pipe, JsonSerializer.Serialize(reply, GitAskPass.Json), stopping)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            // 相手が途中で消えた。受け口は次の問いを待ち続ける。
        }
        catch (Exception ex)
        {
            // 投げっぱなしのタスクなので、ここで拾わないと誰にも見えない。
            System.Diagnostics.Debug.WriteLine($"[askpass] 問いの処理のエラー: {ex}");
        }
        finally
        {
            asker.Cancel();
        }
    }

    private async Task<GitAskPass.Reply> AnswerAsync(Stream pipe, GitAskPass.Request request, CancellationTokenSource asker)
    {
        var invocation = request.Invocation ?? "";
        // 人に聞かずに返す答え（一度だけ）。
        if (invocation.Length > 0 && _answering.TryGetValue(invocation, out var canned))
        {
            _answering[invocation] = null;
            return canned is null ? new GitAskPass.Reply(false) : new GitAskPass.Reply(true, canned);
        }

        // exe は答えを待つあいだ何も送らない。読みが終わる＝相手が居なくなった（git が止められた）ので、入力欄を閉じる。
        _ = WatchDisconnectAsync(pipe, asker);
        var kind = GitAskPass.Classify(request);
        var state = invocation.Length == 0 ? null : _invocations.GetOrAdd(invocation, _ => new InvocationState());
        var offer = OfferFor(request, kind);
        GitCredentialAnswer? answer;
        state?.Time.Begin();
        try
        {
            answer = await _prompter.AskAsync(request, kind, offer, asker.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 入力欄が出せなかった（窓の破棄と競合など）。取り消しとして返し、git は今まで通りの止まり方をする。
            System.Diagnostics.Debug.WriteLine($"[askpass] 入力欄のエラー: {ex}");
            answer = null;
        }
        finally
        {
            state?.Time.End();
        }
        if (answer is null)
            return new GitAskPass.Reply(false);

        if (state is not null)
        {
            // HTTPS はユーザー名とパスワードが別々の問いで来る。パスワードの問いに名前が無いときのために覚えておく。
            if (kind == GitCredentialPromptKind.Text && (request.Prompt ?? "").TrimStart().StartsWith("Username", StringComparison.Ordinal))
                state.UserName = answer.Text;
            if (answer.Remember && offer is not null)
                state.Remember = new PendingRemember(offer, (offer as GitCredentialRemember.Https)?.UserName ?? state.UserName, answer.Text);
        }
        return new GitAskPass.Reply(true, answer.Text);
    }

    /// <summary>「次から聞かれないようにする」を出せるか。覚えさせる係が無い・この PC で使えない・起動の印が無い（git が終わったと
    /// 分からない）ときは出さない。</summary>
    private GitCredentialRemember? OfferFor(GitAskPass.Request request, GitCredentialPromptKind kind)
    {
        if (_registrar is null || string.IsNullOrEmpty(request.Invocation))
            return null;
        if (GitCredentialRemember.From(request, kind) is not { } offer)
            return null;
        try { return _registrar.CanRemember(offer) ? offer : null; }
        catch (Exception) { return null; }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource asker)
    {
        try
        {
            var one = new byte[1];
            while (!asker.IsCancellationRequested)
            {
                if (await pipe.ReadAsync(one, asker.Token).ConfigureAwait(false) == 0)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        try { asker.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _stop.Cancel();

    private sealed record PendingRemember(GitCredentialRemember Remember, string? UserName, string Secret);

    private sealed class InvocationState
    {
        public AskingTime Time { get; } = new();
        public string? UserName { get; set; }
        public PendingRemember? Remember { get; set; }
    }

    /// <summary>人が答えるのに使った時間。同じ git から問いが重なっても（ユーザー名→パスワード）足し合わせる。</summary>
    private sealed class AskingTime
    {
        private readonly object _gate = new();
        private TimeSpan _done;
        private int _active;
        private long _since;

        public void Begin()
        {
            lock (_gate)
                if (_active++ == 0) _since = Stopwatch.GetTimestamp();
        }

        public void End()
        {
            lock (_gate)
                if (--_active == 0) _done += Stopwatch.GetElapsedTime(_since);
        }

        public TimeSpan Total
        {
            get
            {
                lock (_gate)
                    return _active > 0 ? _done + Stopwatch.GetElapsedTime(_since) : _done;
            }
        }
    }
}
