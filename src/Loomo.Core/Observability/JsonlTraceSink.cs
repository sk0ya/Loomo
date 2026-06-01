using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.IO;

namespace sk0ya.Loomo.Core.Observability;

/// <summary>
/// AI操作トレースをセッション毎の追記専用 JSON Lines に書き出す（設計書 §20.2 / §20.4）。
/// <c>%APPDATA%/Loomo/traces/{sessionId}.jsonl</c> に 1行1イベントで追記する。
/// <para>
/// <see cref="Record"/> はチャネルへ投入するだけで即座に返り（fire-and-forget）、単一の
/// バックグラウンドワーカーが順次ファイルへ書き出す。書き込み失敗はエージェント動作を妨げない。
/// </para>
/// <para>
/// 設計書 §20.7：トレースは機微データ。保持ポリシー（セッション数上限でローテーション）を持つ。
/// </para>
/// </summary>
public sealed class JsonlTraceSink : ITraceSink, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // JSONL は1行1イベントなので整形しない。
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;
    private readonly int _maxSessions;
    private readonly Channel<TraceEvent> _channel;
    private readonly Task _worker;

    // 連番はワーカー（単一スレッド）でのみ採番・参照する。
    private readonly ConcurrentDictionary<string, long> _seq = new();

    /// <param name="dir">trace 格納ディレクトリ。null で既定（<see cref="DefaultDir"/>）。</param>
    /// <param name="maxSessions">保持するセッション(ファイル)数の上限。超過分は古い順に削除。0以下で無制限。</param>
    public JsonlTraceSink(string? dir = null, int maxSessions = 200)
    {
        _dir = dir ?? DefaultDir();
        _maxSessions = maxSessions;
        _channel = Channel.CreateUnbounded<TraceEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(ProcessAsync);
    }

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "traces");

    public void Record(string sessionId, string? turnId, string kind, object? payload)
    {
        if (string.IsNullOrEmpty(sessionId)) sessionId = "unknown";

        // 連番(seq)はワーカー側で採番する（生成順 = チャネルの FIFO 順で保たれる）。
        // 時刻は発生時点を残したいのでここで採る。
        var ev = new TraceEvent
        {
            Ts = DateTimeOffset.Now,
            SessionId = sessionId,
            TurnId = turnId,
            Kind = kind,
            Payload = payload,
        };

        // バッファ（チャネル）へ投入するだけ。書き込みはワーカーが行う。
        _channel.Writer.TryWrite(ev);
    }

    private async Task ProcessAsync()
    {
        await foreach (var ev in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { Append(ev); }
            catch { /* ログ記録の失敗はアプリ動作を妨げない */ }
        }
    }

    private void Append(TraceEvent ev)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(ev.SessionId);

        // 既存ファイルが無い＝このセッションのトレース新規作成時のみ、保持上限を適用する。
        var isNewFile = !File.Exists(path);
        if (isNewFile) Rotate();

        // セッション毎の連番を採番。プロセス再起動でセッションを再開した場合は、
        // 既存ファイルの行数から続き番号を割り当て、単調増加（追記専用）の不変条件を保つ。
        if (!_seq.TryGetValue(ev.SessionId, out var next))
            next = isNewFile ? 0L : CountLines(path);
        _seq[ev.SessionId] = next + 1;

        var stamped = ev with { Seq = next };
        File.AppendAllText(path, JsonSerializer.Serialize(stamped, JsonOpts) + Environment.NewLine);
    }

    private static long CountLines(string path)
    {
        long n = 0;
        try { foreach (var _ in File.ReadLines(path)) n++; }
        catch { /* 読めなければ 0 起点 */ }
        return n;
    }

    /// <summary>古いトレースファイルを削除して保持上限を守る。</summary>
    private void Rotate()
    {
        if (_maxSessions <= 0) return;
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.jsonl");
            if (files.Length < _maxSessions) return;
            // 新規ファイル1件ぶんの余地を空けるため、既存は新しい順に (_maxSessions - 1) 件だけ残す。
            foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(_maxSessions - 1))
            {
                try { old.Delete(); } catch { /* 個別の削除失敗は無視 */ }
            }
        }
        catch { /* ローテーション失敗は記録を妨げない */ }
    }

    private string PathFor(string sessionId)
    {
        // パストラバーサル防止：ファイル名に使える文字だけ残す（ConversationStore と同方針）。
        var safe = SafeFileName.Sanitize(sessionId);
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_dir, safe + ".jsonl");
    }

    /// <summary>未書き込みのイベントを書き出してからワーカーを終了する（同期）。</summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _worker.GetAwaiter().GetResult(); }
        catch { /* 終了時のフラッシュ失敗は無視 */ }
    }

    /// <summary>未書き込みのイベントを書き出してからワーカーを終了する（非同期）。</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); }
        catch { /* 終了時のフラッシュ失敗は無視 */ }
    }
}
