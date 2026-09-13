using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Completion;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Ai.Completion;

/// <summary>
/// 入力の先読みを<b>別プロセス</b>のワーカー（<c>sk0ya.Loomo.Completion.Host</c>）へ委ねる窓口。
///
/// <para>同じプロセスで推論を回していたときは、スレッド数をいくら絞っても打鍵が引っかかった——
/// CPU コアだけでなくメモリ帯域も GC も分け合うためで、実測でも単独なら 270ms の生成が
/// アプリの中では 1.5〜3.9 秒に伸びていた。プロセスを分ければ OS がスケジューリングを分離でき、
/// <b>優先度を下げられる</b>（<see cref="ProcessPriorityClass.BelowNormal"/>）。人の入力より
/// 先読みが優先されることは、これで構造的に起こらなくなる。</para>
///
/// <para>ワーカーが落ちても本体は何も失わない（次の依頼で起動し直す）。応答が来なくても
/// 待ち続けない。<b>先読みの不調で入力が止まる経路を作らない</b>のがこのクラスの役目。</para>
/// </summary>
public sealed class FimCompletionClient : IDisposable
{
    /// <summary>ワーカーの実行ファイル名。本体と同じフォルダに置かれる。</summary>
    private const string HostExecutable = "sk0ya.Loomo.Completion.Host.exe";

    /// <summary>1 件の応答をここまで待つ。超えたら諦める——出る頃には手が先へ進んでいる。</summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Editor の120ms待ちのあと、さらに入力が止まるまで待つ。ここを短くするとモデル推論が
    /// 打鍵の合間に始まり、CPUとメモリ帯域を奪って文字入力が引っかかる。
    /// </summary>
    private static readonly TimeSpan TypingQuietPeriod = TimeSpan.FromMilliseconds(350);

    /// <summary>ワーカーが立て続けに落ちるときは諦める（起動の繰り返しで CPU を食わない）。</summary>
    private const int MaxStartFailures = 3;

    /// <summary>これだけ使われなければワーカーを畳む。モデルを抱えたまま 500MB 超を
    /// 居座らせない——次に要るときは 2 秒ほどで起動し直せる。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<FimProtocol.Response>> _pending = new();

    private Process? _process;
    private Timer? _idleTimer;
    private DateTime _lastUsedUtc;
    private string? _startedFor;
    private long _nextId;
    private int _startFailures;
    private bool _disposed;

    /// <summary>直近の応答の内訳（診断用）。</summary>
    public FimProtocol.Response LastResponse { get; private set; }

    /// <summary>ワーカーが動いているか。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    /// <summary>
    /// 先読みを 1 件求める。出せないときは null——設定が無効、モデルが無い、ワーカーが落ちている、
    /// 打鍵で追い越された、応答が遅すぎた、のいずれも同じ「何も出さない」に畳む。
    /// </summary>
    public async Task<string?> CompleteAsync(
        InlineCompletionSettings settings, IReadOnlyList<string> lines, int line, int column,
        CancellationToken ct)
    {
        if (_disposed || settings is null || !settings.Enabled) return null;
        if (string.IsNullOrWhiteSpace(settings.ModelPath) || !File.Exists(settings.ModelPath)) return null;
        if (lines is null || lines.Count == 0) return null;

        // Editorのタイマーを通過しただけではまだ送らない。次の打鍵でキャンセルされれば、
        // プロセス起動もモデル推論も発生しない。入力の滑らかさを候補の早さより優先する。
        try
        {
            await Task.Delay(TypingQuietPeriod, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }

        var prompt = FimPrompt.Build(lines, line, column, settings.PrefixLines, settings.SuffixLines);
        var id = Interlocked.Increment(ref _nextId);

        var tcs = new TaskCompletionSource<FimProtocol.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            if (!TrySend(settings, id, prompt, Math.Max(1, settings.MaxTokens))) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseTimeout);
            using var registration = timeout.Token.Register(() =>
            {
                // TCS を先に決着させ、成功応答と競合した場合は取り消し通知を送らない。
                if (tcs.TrySetResult(default)) Cancel(id);
            });

            var response = await tcs.Task.ConfigureAwait(false);
            if (response.Id != id || response.Text is not { Length: > 0 } raw) return null;

            LastResponse = response;

            var current = lines[Math.Clamp(line, 0, lines.Count - 1)];
            int col = Math.Clamp(column, 0, current.Length);
            return FimCandidateFilter.Clean(raw, current[..col], current[col..]);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private bool TrySend(InlineCompletionSettings settings, long id, string prompt, int maxTokens)
    {
        lock (_gate)
        {
            if (!EnsureStarted(settings)) return false;
            _lastUsedUtc = DateTime.UtcNow;
            try
            {
                _process!.StandardInput.WriteLine(FimProtocol.Serialize(new FimProtocol.Request(id, prompt, maxTokens)));
                _process.StandardInput.Flush();
                return true;
            }
            catch
            {
                // パイプが壊れた＝ワーカーが落ちた。次の依頼で起動し直す。
                TearDown();
                return false;
            }
        }
    }

    /// <summary>画面側で待たなくなった依頼を、ワーカーの生成ループにも伝える。</summary>
    private void Cancel(long id)
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false }) return;
            try
            {
                _process.StandardInput.WriteLine(FimProtocol.Serialize(new FimProtocol.Cancellation(id)));
                _process.StandardInput.Flush();
            }
            catch
            {
                TearDown();
            }
        }
    }

    private bool EnsureStarted(InlineCompletionSettings settings)
    {
        if (_process is { HasExited: false } && _startedFor == settings.ModelPath) return true;
        if (_process is not null) TearDown();
        if (_startFailures >= MaxStartFailures) return false;

        var executable = Path.Combine(AppContext.BaseDirectory, HostExecutable);
        if (!File.Exists(executable))
        {
            _startFailures = MaxStartFailures;   // 置かれていない環境で毎回試さない
            return false;
        }

        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(settings.ModelPath!);
        info.ArgumentList.Add(settings.Threads.ToString());
        info.ArgumentList.Add(settings.PrefillThreads.ToString());

        try
        {
            var process = Process.Start(info);
            if (process is null) { _startFailures++; return false; }

            // 本体が落ちてもワーカーを残さない（Windows の Job Object。他の OS では何もしない）。
            if (OperatingSystem.IsWindows()) CompletionHostJob.Assign(process);

            // ここが別プロセスにした眼目。人の入力（Normal）より必ず後回しにする。
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            _process = process;
            _startedFor = settings.ModelPath;
            _startFailures = 0;

            StartReader(process);
            StartErrorDrain(process);
            StartIdleWatch();
            return true;
        }
        catch
        {
            _startFailures++;
            return false;
        }
    }

    /// <summary>使われないまま置かれているワーカーを畳む。</summary>
    private void StartIdleWatch()
    {
        _idleTimer?.Dispose();
        _idleTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_process is null || _pending.Count > 0) return;
                if (DateTime.UtcNow - _lastUsedUtc < IdleTimeout) return;
                TearDown();
            }
        }, null, IdleTimeout, IdleTimeout);
    }

    /// <summary>ワーカーの応答を読み続ける。行が来なくなったら（＝落ちたら）待っている依頼を畳む。</summary>
    private void StartReader(Process process)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (process.StandardOutput.ReadLine() is { } line)
                {
                    if (FimProtocol.ReadResponse(line) is { } response)
                        Complete(response);
                }
            }
            catch { /* パイプが閉じた */ }
            finally
            {
                // 残っている依頼を「出せなかった」で決着させる。待ち続けさせない。
                foreach (var key in _pending.Keys)
                {
                    if (_pending.TryRemove(key, out var waiter)) waiter.TrySetResult(default);
                }
            }
        })
        { IsBackground = true, Name = "FimHostStdout" };
        thread.Start();
    }

    /// <summary>ワーカーの stderr を読み捨てる。放っておくとバッファが詰まって相手が止まる。</summary>
    private void StartErrorDrain(Process process)
    {
        var thread = new Thread(() =>
        {
            try { while (process.StandardError.ReadLine() is { } line) Debug.WriteLine(line); }
            catch { }
        })
        { IsBackground = true, Name = "FimHostStderr" };
        thread.Start();
    }

    private void Complete(in FimProtocol.Response response)
    {
        if (_pending.TryRemove(response.Id, out var waiter)) waiter.TrySetResult(response);
    }

    private void TearDown()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        var process = _process;
        _process = null;
        _startedFor = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) TearDown();
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiter)) waiter.TrySetResult(default);
        }
    }
}
