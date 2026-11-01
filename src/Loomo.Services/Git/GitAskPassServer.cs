using System;
using System.Collections.Generic;
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
/// <para>exe が見つからなければ（ビルドの途中など）何も足さない——そのときは今まで通り、対話が要る操作はその場で失敗する。</para>
/// </summary>
public sealed class GitAskPassServer : IDisposable
{
    private readonly IGitCredentialPrompter _prompter;
    private readonly string _helperPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _started;

    public GitAskPassServer(IGitCredentialPrompter prompter, string helperPath, string? pipeName = null)
    {
        _prompter = prompter;
        _helperPath = helperPath;
        PipeName = pipeName ?? $"loomo-askpass-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    public string PipeName { get; }

    /// <summary>git に渡す環境変数。<paramref name="context"/> は入力欄に出す「どの操作か」（例 <c>git push</c>）。
    /// exe が無ければ null（何も足さない）。初めて呼ばれたときに受け口を開く。</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentFor(string context)
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
        };
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

                // exe は答えを待つあいだ何も送らない。読みが終わる＝相手が居なくなった（git が止められた）ので、入力欄を閉じる。
                _ = WatchDisconnectAsync(pipe, asker);
                var kind = GitAskPass.Classify(request);
                var answer = await _prompter.AskAsync(request, kind, asker.Token).ConfigureAwait(false);
                var reply = answer is null ? new GitAskPass.Reply(false) : new GitAskPass.Reply(true, answer);
                await GitAskPass.WriteLineAsync(pipe, JsonSerializer.Serialize(reply, GitAskPass.Json), stopping)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            // 相手が途中で消えた。受け口は次の問いを待ち続ける。
        }
        finally
        {
            asker.Cancel();
        }
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
}
