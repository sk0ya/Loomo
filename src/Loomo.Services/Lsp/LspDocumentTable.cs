using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// URI 別に開いている文書と、それを見ているハンドル（＝ビュー）を管理する。
///
/// <para>Loomo は分割ビューや切り離しウィンドウで**同じファイルを別バッファとして2枚開ける**が、
/// LSP の文書同期は1 URI につき1本しか成立しない。そこで（設計書 §30.3.4）：</para>
/// <list type="bullet">
/// <item>1つの URI に対し <c>didOpen</c> は1回だけ。以降の <see cref="Open"/> は参照カウントを増やす。</item>
/// <item>最初のハンドルが**書き手**。以降は読み手で <c>UpdateText</c> は no-op。</item>
/// <item>書き手が Dispose されたら残りの先頭へ移譲し、その時点のテキストで <c>didChange</c> を送る。</item>
/// <item>参照カウントが 0 で <c>didClose</c>。</item>
/// <item>診断は URI 単位なので**全ハンドルへ配る**（読み手のビューにも波線が出る）。</item>
/// </list>
///
/// <para><b>スレッド:</b> スレッドセーフ。ハンドルのイベントは背景スレッドで発火する。</para>
/// </summary>
internal sealed class LspDocumentTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LspDocumentEntry> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly LspClientPool _pool;
    private readonly Func<string, LspServerDef?> _resolveServer;
    private readonly Func<string, string> _resolveRoot;
    private readonly Action<string> _log;

    public LspDocumentTable(
        LspClientPool pool,
        Func<string, LspServerDef?> resolveServer,
        Func<string, string> resolveRoot,
        Action<string> log)
    {
        _pool = pool;
        _resolveServer = resolveServer;
        _resolveRoot = resolveRoot;
        _log = log;
    }

    /// <summary>
    /// <paramref name="filePath"/> の文書を開く（すでに開いていれば参加する）。この拡張子に
    /// 対応するサーバーが無い／起動できない場合は null。
    /// </summary>
    public ILspDocument? Open(string filePath, string initialText)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var full = Path.GetFullPath(filePath);
        var ext = LspExtensions.NormalizeExt(Path.GetExtension(full));
        var def = _resolveServer(ext);
        if (def is null) return null;

        var uri = PathToUri(full);
        LspDocumentEntry entry;
        LspDocumentHandle handle;
        bool isNewEntry = false;

        lock (_gate)
        {
            if (_docs.TryGetValue(uri, out var existing) && existing.Client.IsRunning)
            {
                entry = existing;
            }
            else
            {
                var pooled = _pool.Acquire(def, _resolveRoot(full));
                if (pooled is null) return null;
                entry = new LspDocumentEntry(this, uri, full, ext, def.LanguageId, pooled, initialText);
                _docs[uri] = entry;
                isNewEntry = true;
            }
            handle = entry.AddHandle();
        }

        if (isNewEntry) _ = OpenOnServerAsync(entry);
        return handle;
    }

    /// <summary>その拡張子のサーバーが設定されているか。</summary>
    public bool IsServerAvailableFor(string extension) => _resolveServer(LspExtensions.NormalizeExt(extension)) is not null;

    /// <summary>URI から開いている文書を引く（呼び出し階層などが所属サーバーを知るため）。</summary>
    public LspDocumentEntry? Find(string uri)
    {
        lock (_gate) return _docs.GetValueOrDefault(uri);
    }

    /// <summary><c>publishDiagnostics</c> を該当文書の全ハンドルへ配る。</summary>
    public void OnDiagnostics(string uri, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        LspDocumentEntry? entry;
        lock (_gate) entry = _docs.GetValueOrDefault(uri);
        entry?.PublishDiagnostics(diagnostics);
    }

    /// <summary>
    /// サーバーがクラッシュした。そのサーバーが担っていた文書を、作り直したプロセスへ
    /// 透過的に載せ替える（再初期化 → 最新テキストで <c>didOpen</c> をリプレイ）。
    /// </summary>
    public async Task OnClientDiedAsync(PooledLspClient dead)
    {
        List<LspDocumentEntry> orphans;
        lock (_gate)
            orphans = _docs.Values.Where(e => ReferenceEquals(e.Client, dead)).ToList();
        if (orphans.Count == 0) return;

        foreach (var e in orphans) e.MarkDisconnected("LSP: 接続が切れました（再接続中…）");

        // 再解決してから作り直す：バックオフ中にユーザーが :LspAdd などで割り当てを変えている可能性がある。
        var def = _resolveServer(orphans[0].Extension);
        if (def is null) return;

        var fresh = await _pool.ReconnectAsync(dead, def);
        if (fresh is null)
        {
            foreach (var e in orphans) e.MarkDisconnected($"LSP: {def.Executable} に再接続できませんでした");
            return;
        }

        foreach (var entry in orphans)
        {
            entry.Rebind(fresh);
            await OpenOnServerAsync(entry);
        }
    }

    /// <summary>
    /// 拡張子の割り当てが変わった。開いている該当文書を**その場で**新しいサーバーへ載せ替える
    /// （設定画面での変更に「開き直し」を要求しないため）。
    /// </summary>
    public async Task ReopenExtensionAsync(string extension)
    {
        var ext = LspExtensions.NormalizeExt(extension);
        List<LspDocumentEntry> affected;
        lock (_gate)
            affected = _docs.Values
                .Where(e => string.Equals(e.Extension, ext, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (affected.Count == 0) return;

        var def = _resolveServer(ext);
        foreach (var entry in affected)
        {
            await CloseOnServerAsync(entry);
            if (def is null)
            {
                // 割り当てが外された → 文書は閉じたまま。ビューは IsConnected=false で LSP 無しに戻る。
                lock (_gate) _docs.Remove(entry.Uri);
                entry.MarkDisconnected($"LSP: {ext} のサーバー設定が解除されました");
                continue;
            }
            var pooled = _pool.Acquire(def, _resolveRoot(entry.FilePath));
            if (pooled is null)
            {
                lock (_gate) _docs.Remove(entry.Uri);
                entry.MarkDisconnected($"LSP: {def.Executable} を起動できませんでした");
                continue;
            }
            entry.Rebind(pooled, def.LanguageId);
            await OpenOnServerAsync(entry);
        }
    }

    /// <summary>ワークスペース切替。開いている文書をすべて手放す（サーバーはプール側で即時終了）。</summary>
    public void Clear()
    {
        List<LspDocumentEntry> all;
        lock (_gate)
        {
            all = _docs.Values.ToList();
            _docs.Clear();
        }
        foreach (var e in all) e.MarkDisconnected("LSP: ワークスペースが切り替わりました");
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    private async Task OpenOnServerAsync(LspDocumentEntry entry)
    {
        var client = entry.Client;
        if (!await client.Ready)
        {
            entry.MarkDisconnected("LSP: init failed");
            return;
        }
        try
        {
            _log($"[LSP] didOpen uri={entry.Uri}");
            await client.Client.OpenDocumentAsync(entry.Uri, entry.LanguageId, entry.Text);
            entry.MarkOpened();
            _log("[LSP] document ready");
        }
        catch (Exception ex)
        {
            _log($"[LSP] didOpen failed: {ex.Message}");
            entry.MarkDisconnected($"LSP: didOpen failed ({ex.Message})");
        }
    }

    private async Task CloseOnServerAsync(LspDocumentEntry entry)
    {
        if (!entry.Opened) return;
        try { await entry.Client.Client.CloseDocumentAsync(entry.Uri); } catch { }
        entry.MarkClosed();
    }

    /// <summary>ハンドルが 1 つ消えたときの後始末（<see cref="LspDocumentHandle.Dispose"/> から）。</summary>
    internal void ReleaseHandle(LspDocumentEntry entry, LspDocumentHandle handle)
    {
        bool last;
        lock (_gate)
        {
            last = entry.RemoveHandle(handle);   // 書き手の移譲もここで行う
            if (last) _docs.Remove(entry.Uri);
        }
        if (!last) return;

        _ = Task.Run(async () =>
        {
            await CloseOnServerAsync(entry);
            _pool.Release(entry.Client);
        });
    }

    private static string PathToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}

/// <summary>1 URI ぶんの状態。テキストの正本・版番号・診断・ハンドル一覧を持つ。</summary>
internal sealed class LspDocumentEntry
{
    private readonly object _gate = new();
    private readonly List<LspDocumentHandle> _handles = [];

    public LspDocumentTable Table { get; }
    public string Uri { get; }
    public string FilePath { get; }
    public string Extension { get; }
    public string LanguageId { get; private set; }
    public PooledLspClient Client { get; private set; }
    public string Text { get; private set; }
    public bool Opened { get; private set; }
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; private set; } = [];

    private int _version = 1;

    public LspDocumentEntry(
        LspDocumentTable table,
        string uri, string filePath, string extension, string languageId,
        PooledLspClient client, string text)
    {
        Table = table;
        Uri = uri;
        FilePath = filePath;
        Extension = extension;
        LanguageId = languageId;
        Client = client;
        Text = text;
    }

    public LspDocumentHandle AddHandle()
    {
        lock (_gate)
        {
            var handle = new LspDocumentHandle(this, isWriter: _handles.Count == 0);
            _handles.Add(handle);
            return handle;
        }
    }

    /// <summary>ハンドルを外す。書き手なら次へ移譲する。最後の1つだったら true。</summary>
    public bool RemoveHandle(LspDocumentHandle handle)
    {
        LspDocumentHandle? promoted = null;
        bool last;
        lock (_gate)
        {
            _handles.Remove(handle);
            last = _handles.Count == 0;
            if (!last && handle.IsWriter)
            {
                promoted = _handles[0];
                promoted.IsWriter = true;
            }
        }
        // 移譲した先のテキストがサーバーの持つ内容と食い違わないよう、現在の正本を送り直す。
        if (promoted is not null && Opened && Client.IsRunning)
            _ = Client.Client.ChangeDocumentAsync(Uri, Interlocked.Increment(ref _version), Text);
        return last;
    }

    /// <summary>書き手からのテキスト更新。<c>didChange</c> を送る。</summary>
    public void UpdateText(string text)
    {
        Text = text;
        if (!Opened || !Client.IsRunning) return;
        _ = Client.Client.ChangeDocumentAsync(Uri, Interlocked.Increment(ref _version), text);
    }

    public void MarkOpened()
    {
        Opened = true;
        NotifyState("LSP: ready");
    }

    public void MarkClosed()
    {
        Opened = false;
        NotifyState(null);
    }

    public void MarkDisconnected(string? message)
    {
        Opened = false;
        NotifyState(message);
    }

    /// <summary>クラッシュ後・サーバー変更後に別プロセスへ載せ替える。</summary>
    public void Rebind(PooledLspClient client, string? languageId = null)
    {
        Client = client;
        if (languageId is not null) LanguageId = languageId;
        Opened = false;
        _version = 1;
        NotifyState(null);
    }

    public void PublishDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        foreach (var h in Snapshot()) h.RaiseDiagnostics(diagnostics);
    }

    private void NotifyState(string? message)
    {
        foreach (var h in Snapshot())
        {
            if (message is not null) h.RaiseStatus(message);
            h.RaiseStateChanged();
        }
    }

    private LspDocumentHandle[] Snapshot()
    {
        lock (_gate) return _handles.ToArray();
    }
}
