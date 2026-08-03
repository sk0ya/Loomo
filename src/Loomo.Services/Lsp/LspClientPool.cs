using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// プール内の言語サーバー1本。<see cref="LspClient"/> の寿命と初期化完了、参照している文書数を持つ。
/// </summary>
internal sealed class PooledLspClient
{
    public required string Executable { get; init; }
    public required string[] Args { get; init; }
    public required string Root { get; init; }
    public required ILspClient Client { get; init; }

    /// <summary><c>initialize</c>/<c>initialized</c> が終わると完了する。失敗時は false。</summary>
    public required Task<bool> Ready { get; set; }

    /// <summary>この (実行ファイル, ルート) を参照している文書数。0 でアイドル計測が始まる。</summary>
    public int RefCount;

    /// <summary>参照が 0 になった時刻。0 でない間は null。</summary>
    public DateTime? IdleSince;

    public bool IsRunning => Client.IsRunning;
    public LspServerRuntimeState State { get; set; } = LspServerRuntimeState.Starting;
    public string? LastError { get; set; }
    public int ReconnectAttempt { get; set; }
    public bool ReconnectRequested { get; set; }
}

/// <summary>
/// <c>(実行ファイル, ワークスペースルート)</c> をキーにした言語サーバープール。**拡張子ではない** —
/// Roslyn は <c>.cs</c>/<c>.csx</c> を、typescript-language-server は <c>.ts/.tsx/.js/.jsx</c> を
/// 1プロセスで賄うため。同じソリューションの <c>.cs</c> を N 枚開いても Roslyn は1本、初期化と
/// プロジェクト解析も1回になる。
///
/// <para>参照が 0 になってもすぐには落とさず <see cref="IdleTimeout"/>（既定5分）維持する
/// （タブを閉じて開き直すたびに Roslyn を再起動しないため）。ワークスペース切替時は
/// <see cref="DisposeAll"/> で即時終了。</para>
///
/// <para><b>スレッド:</b> すべてのメンバはスレッドセーフ。イベントは背景スレッドで発火する。</para>
/// </summary>
internal sealed class LspClientPool : IDisposable
{
    /// <summary>最後の文書が閉じてからサーバーを落とすまでの猶予。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<(string Executable, string Root), PooledLspClient> _clients = new();
    /// <summary>起動に失敗した (実行ファイル, ルート) と、その理由。設定画面へ出すために保持する。</summary>
    private readonly Dictionary<(string Executable, string Root), string> _startFailures = new();
    private readonly Dictionary<string, int> _reconnectAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<string>> _workspaceFolders;
    private readonly Func<LspServerDef, string, ILspClient> _connect;
    private readonly Action<string> _log;
    private readonly Timer _idleSweep;
    private bool _disposed;

    /// <summary>プールに登録済みのクライアントが予期せず終了した（クラッシュ）。背景スレッド発火。</summary>
    public event Action<PooledLspClient>? ClientDied;

    /// <summary>サーバーが起動・初期化完了・終了した。背景スレッド発火。</summary>
    public event Action? StateChanged;

    /// <summary><c>publishDiagnostics</c>。背景スレッド発火。</summary>
    public event Action<string, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished;

    /// <param name="connect">実サーバーへの接続を作る。テストは差し替えてプロセスを起動せずに検証する。</param>
    public LspClientPool(
        Func<IReadOnlyList<string>> workspaceFolders,
        Action<string> log,
        Func<LspServerDef, string, ILspClient>? connect = null)
    {
        _workspaceFolders = workspaceFolders;
        // 起動は必ず PATH 解決後のフルパスで。素の名前だと .cmd/.bat シム（npm -g / winget）が
        // 起動できず、UI は「接続待ち」のまま永久に進まない（ExecutableResolver.Resolve 参照）。
        _connect = connect ?? ((def, root) =>
            new Editor.Controls.Lsp.LspClient(
                ExecutableResolver.Resolve(def.Executable) ?? def.Executable, def.Args, root));
        _log = log;
        _idleSweep = new Timer(_ => SweepIdle(), null, SweepInterval, SweepInterval);
    }

    /// <summary>現在生きているクライアント一覧（ワークスペーススコープの問い合わせ用）。</summary>
    public IReadOnlyList<PooledLspClient> Running
    {
        get { lock (_gate) return _clients.Values.Where(c => c.IsRunning).ToList(); }
    }

    /// <summary>
    /// 稼働中のサーバーに加え、**起動そのものに失敗した組み合わせ**も <see cref="LspServerRuntimeState.Failed"/>
    /// として返す。起動失敗はログへ落ちるだけで、UI 側には「接続待ち」しか出ない期間があった。
    /// </summary>
    public IReadOnlyList<LspServerRuntimeStatus> Statuses
    {
        get
        {
            lock (_gate)
                return
                [
                    .. _clients.Values.Select(c => new LspServerRuntimeStatus(
                        c.Executable, c.Root, c.State, c.LastError, c.ReconnectAttempt)),
                    .. _startFailures
                        .Where(f => !_clients.ContainsKey(f.Key))
                        .Select(f => new LspServerRuntimeStatus(
                            f.Key.Executable, f.Key.Root, LspServerRuntimeState.Failed, f.Value, 0)),
                ];
        }
    }

    /// <summary>診断応答など、プロジェクト読込後の実要求へ応答できた時点でreadyへ進める。</summary>
    public void MarkReady(PooledLspClient client)
    {
        bool changed = false;
        lock (_gate)
        {
            var key = (client.Executable, client.Root);
            if (_clients.TryGetValue(key, out var current) && ReferenceEquals(current, client) &&
                current.State == LspServerRuntimeState.ProjectLoading)
            {
                current.State = LspServerRuntimeState.Ready;
                current.LastError = null;
                changed = true;
            }
        }
        if (changed) NotifyStateChanged();
    }

    /// <summary>
    /// <paramref name="def"/> と <paramref name="root"/> に対応するクライアントを取得（無ければ起動）し、
    /// 参照カウントを1増やす。起動に失敗したら null。
    /// </summary>
    public PooledLspClient? Acquire(LspServerDef def, string root)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var key = (def.Executable, root);
            if (_clients.TryGetValue(key, out var existing) && existing.IsRunning)
            {
                existing.RefCount++;
                existing.IdleSince = null;
                return existing;
            }
            if (existing is not null) _clients.Remove(key);   // 死んでいた個体を置き換える

            var created = Create(def, root);
            if (created is null) return null;
            created.RefCount = 1;
            _clients[key] = created;
            return created;
        }
    }

    /// <summary>参照カウントを1減らす。0 になったらアイドル計測を始める（即時終了はしない）。</summary>
    public void Release(PooledLspClient client)
    {
        lock (_gate)
        {
            if (--client.RefCount <= 0)
            {
                client.RefCount = 0;
                client.IdleSince = DateTime.UtcNow;
            }
        }
    }

    /// <summary>設定UIからの手動再起動。クラッシュ時と同じClientDied経路で文書を再送する。</summary>
    public bool Restart(string executable)
    {
        List<PooledLspClient> targets;
        lock (_gate)
        {
            targets = _clients.Where(x => x.Key.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value).ToList();
            if (targets.Count == 0) return false;
            foreach (var target in targets)
            {
                target.State = LspServerRuntimeState.Reconnecting;
                target.LastError = null;
                // Dispose に伴う Exited と、このメソッドの明示的な ClientDied を二重処理しない。
                target.ReconnectRequested = true;
            }
            _reconnectAttempts[executable] = 0;
        }
        NotifyStateChanged();
        foreach (var target in targets)
        {
            SafeDispose(target);
            ClientDied?.Invoke(target);
        }
        return true;
    }

    /// <summary>
    /// クラッシュしたクライアントを同じキーで作り直す。試行上限（3回）を超えた・すでに誰かが
    /// 作り直していた場合は null。参照カウントは死んだ個体から引き継ぐ。
    /// 起動直後に毎回落ちるサーバーで CPU を焼かないよう 0.5s/1.5s/4.5s とバックオフする。
    /// </summary>
    public async Task<PooledLspClient?> ReconnectAsync(PooledLspClient dead, LspServerDef def)
    {
        int attempts;
        bool giveUp = false;
        lock (_gate)
        {
            if (_disposed) return null;
            var key = (def.Executable, dead.Root);
            if (_clients.TryGetValue(key, out var alive) && !ReferenceEquals(alive, dead) && alive.IsRunning)
                return alive;   // 通常の再オープンが先に立て直していた

            attempts = _reconnectAttempts.GetValueOrDefault(def.Executable);
            dead.State = LspServerRuntimeState.Reconnecting;
            dead.ReconnectAttempt = attempts + 1;
            if (attempts >= MaxReconnectAttempts)
            {
                dead.State = LspServerRuntimeState.Failed;
                dead.LastError = $"{attempts}回の再接続に失敗しました。";
                _log($"[LSP] {def.Executable}: giving up after {attempts} reconnect attempts");
                giveUp = true;
            }
            else
            {
                _reconnectAttempts[def.Executable] = attempts + 1;
            }
        }

        NotifyStateChanged();
        if (giveUp) return null;

        await Task.Delay(500 * (int)Math.Pow(3, attempts));

        PooledLspClient? created;
        lock (_gate)
        {
            if (_disposed) return null;
            var key = (def.Executable, dead.Root);
            if (_clients.TryGetValue(key, out var current) && !ReferenceEquals(current, dead) && current.IsRunning)
                return current;

            created = Create(def, dead.Root);
            if (created is null)
            {
                dead.State = LspServerRuntimeState.Failed;
                dead.LastError = $"{def.Executable} を起動できませんでした。";
            }
            else
            {
                created.RefCount = dead.RefCount;
                created.ReconnectAttempt = attempts + 1;
                _clients[key] = created;
                _log($"[LSP] Process restarted: {def.Executable} (attempt {attempts + 1})");
            }
        }
        NotifyStateChanged();
        return created;
    }

    /// <summary>ワークスペース切替時。全サーバーを即時終了する。</summary>
    public void DisposeAll()
    {
        List<PooledLspClient> doomed;
        lock (_gate)
        {
            doomed = _clients.Values.ToList();
            _clients.Clear();
            _reconnectAttempts.Clear();
        }
        foreach (var c in doomed) SafeDispose(c);
        if (doomed.Count > 0) NotifyStateChanged();
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    /// <summary>lock 内から呼ぶこと。</summary>
    private PooledLspClient? Create(LspServerDef def, string root)
    {
        ILspClient client;
        try
        {
            client = _connect(def, root);
        }
        catch (Exception ex)
        {
            _log($"[LSP] Failed to start {def.Executable}: {ex.Message}");
            lock (_gate) _startFailures[(def.Executable, root)] = $"起動に失敗しました: {ex.Message}";
            NotifyStateChanged();
            return null;
        }
        lock (_gate) _startFailures.Remove((def.Executable, root));

        var pooled = new PooledLspClient
        {
            Executable = def.Executable,
            Args = def.Args,
            Root = root,
            Client = client,
            Ready = Task.FromResult(false),
        };
        pooled.State = LspServerRuntimeState.Starting;
        pooled.Ready = InitializeAsync(pooled, root);

        client.DiagnosticsChanged += (_, e) =>
        {
            if (pooled.State == LspServerRuntimeState.ProjectLoading)
            {
                pooled.State = LspServerRuntimeState.Ready;
                NotifyStateChanged();
            }
            DiagnosticsPublished?.Invoke(e.Uri, e.Diagnostics);
        };
        client.Exited += () => OnClientExited(pooled);
        _log($"[LSP] Process started: {def.Executable} (root={root})");
        return pooled;
    }

    private async Task<bool> InitializeAsync(PooledLspClient pooled, string root)
    {
        try
        {
            pooled.State = LspServerRuntimeState.Initializing;
            NotifyStateChanged();
            var rootUri = new Uri(Path.GetFullPath(root)).AbsoluteUri;
            var folders = _workspaceFolders();
            _log($"[LSP] initialize rootUri={rootUri} workspaceFolders=" +
                 (folders is { Count: > 0 } ? string.Join(" | ", folders) : "(fallback)"));
            await pooled.Client.InitializeAsync(rootUri, folders);
            pooled.State = LspServerRuntimeState.ProjectLoading;
            pooled.LastError = null;
            _log("[LSP] initialize OK");
            lock (_gate) _reconnectAttempts[pooled.Executable] = 0;   // 正常初期化＝健全に戻った証拠
            NotifyStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            pooled.State = LspServerRuntimeState.Failed;
            pooled.LastError = ex.Message;
            _log($"[LSP] initialize failed: {ex.Message}");
            NotifyStateChanged();
            return false;
        }
    }

    private void OnClientExited(PooledLspClient pooled)
    {
        if (pooled.ReconnectRequested) return;
        bool tracked;
        lock (_gate)
        {
            var key = (pooled.Executable, pooled.Root);
            tracked = _clients.TryGetValue(key, out var current) && ReferenceEquals(current, pooled);
        }
        if (!tracked) return;   // すでに置き換え済み・破棄済みの個体からの遅れたイベント

        _log($"[LSP] {pooled.Executable} exited unexpectedly");
        pooled.State = LspServerRuntimeState.Reconnecting;
        pooled.LastError = "言語サーバープロセスが予期せず終了しました。";
        // 応答待ちの要求を解決しておく（LspProcess.Dispose だけが _pending をキャンセルする）。
        // これをしないとクラッシュ時に飛んでいた hover/completion が永久に待ち続ける。
        SafeDispose(pooled);
        NotifyStateChanged();
        ClientDied?.Invoke(pooled);
    }

    /// <summary>購読側がプールへ再入しても、_gate 内でデッドロックしないよう非同期通知する。</summary>
    private void NotifyStateChanged()
        => ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke());

    private void SweepIdle()
    {
        List<PooledLspClient> doomed = [];
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var (key, c) in _clients.ToList())
            {
                if (c.RefCount > 0 || c.IdleSince is not { } since) continue;
                if (now - since < IdleTimeout) continue;
                _clients.Remove(key);
                doomed.Add(c);
            }
        }
        foreach (var c in doomed)
        {
            _log($"[LSP] {c.Executable}: idle for {IdleTimeout.TotalMinutes:0} min, shutting down");
            SafeDispose(c);
        }
        if (doomed.Count > 0) NotifyStateChanged();
    }

    private static void SafeDispose(PooledLspClient c)
    {
        try { c.Client.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _idleSweep.Dispose();
        DisposeAll();
    }
}
