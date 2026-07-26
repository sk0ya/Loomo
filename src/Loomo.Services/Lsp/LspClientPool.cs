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
        _connect = connect ?? ((def, root) =>
            new Editor.Controls.Lsp.LspClient(def.Executable, def.Args, root));
        _log = log;
        _idleSweep = new Timer(_ => SweepIdle(), null, SweepInterval, SweepInterval);
    }

    /// <summary>現在生きているクライアント一覧（ワークスペーススコープの問い合わせ用）。</summary>
    public IReadOnlyList<PooledLspClient> Running
    {
        get { lock (_gate) return _clients.Values.Where(c => c.IsRunning).ToList(); }
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

    /// <summary>
    /// クラッシュしたクライアントを同じキーで作り直す。試行上限（3回）を超えた・すでに誰かが
    /// 作り直していた場合は null。参照カウントは死んだ個体から引き継ぐ。
    /// 起動直後に毎回落ちるサーバーで CPU を焼かないよう 0.5s/1.5s/4.5s とバックオフする。
    /// </summary>
    public async Task<PooledLspClient?> ReconnectAsync(PooledLspClient dead, LspServerDef def)
    {
        int attempts;
        lock (_gate)
        {
            if (_disposed) return null;
            var key = (def.Executable, dead.Root);
            if (_clients.TryGetValue(key, out var alive) && alive.IsRunning)
                return alive;   // 通常の再オープンが先に立て直していた

            attempts = _reconnectAttempts.GetValueOrDefault(def.Executable);
            if (attempts >= MaxReconnectAttempts)
            {
                _log($"[LSP] {def.Executable}: giving up after {attempts} reconnect attempts");
                return null;
            }
            _reconnectAttempts[def.Executable] = attempts + 1;
        }

        await Task.Delay(500 * (int)Math.Pow(3, attempts));

        lock (_gate)
        {
            if (_disposed) return null;
            var key = (def.Executable, dead.Root);
            if (_clients.TryGetValue(key, out var current) && current.IsRunning)
                return current;

            var created = Create(def, dead.Root);
            if (created is null) return null;
            created.RefCount = dead.RefCount;
            _clients[key] = created;
            _log($"[LSP] Process restarted: {def.Executable} (attempt {attempts + 1})");
            return created;
        }
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
        if (doomed.Count > 0) StateChanged?.Invoke();
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
            return null;
        }

        var pooled = new PooledLspClient
        {
            Executable = def.Executable,
            Args = def.Args,
            Root = root,
            Client = client,
            Ready = Task.FromResult(false),
        };
        pooled.Ready = InitializeAsync(pooled, root);

        client.DiagnosticsChanged += (_, e) => DiagnosticsPublished?.Invoke(e.Uri, e.Diagnostics);
        client.Exited += () => OnClientExited(pooled);
        _log($"[LSP] Process started: {def.Executable} (root={root})");
        return pooled;
    }

    private async Task<bool> InitializeAsync(PooledLspClient pooled, string root)
    {
        try
        {
            var rootUri = new Uri(Path.GetFullPath(root)).AbsoluteUri;
            var folders = _workspaceFolders();
            _log($"[LSP] initialize rootUri={rootUri} workspaceFolders=" +
                 (folders is { Count: > 0 } ? string.Join(" | ", folders) : "(fallback)"));
            await pooled.Client.InitializeAsync(rootUri, folders);
            _log("[LSP] initialize OK");
            lock (_gate) _reconnectAttempts[pooled.Executable] = 0;   // 正常初期化＝健全に戻った証拠
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _log($"[LSP] initialize failed: {ex.Message}");
            StateChanged?.Invoke();
            return false;
        }
    }

    private void OnClientExited(PooledLspClient pooled)
    {
        bool tracked;
        lock (_gate)
        {
            var key = (pooled.Executable, pooled.Root);
            tracked = _clients.TryGetValue(key, out var current) && ReferenceEquals(current, pooled);
            if (tracked) _clients.Remove(key);
        }
        if (!tracked) return;   // すでに置き換え済み・破棄済みの個体からの遅れたイベント

        _log($"[LSP] {pooled.Executable} exited unexpectedly");
        // 応答待ちの要求を解決しておく（LspProcess.Dispose だけが _pending をキャンセルする）。
        // これをしないとクラッシュ時に飛んでいた hover/completion が永久に待ち続ける。
        SafeDispose(pooled);
        StateChanged?.Invoke();
        ClientDied?.Invoke(pooled);
    }

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
        if (doomed.Count > 0) StateChanged?.Invoke();
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
