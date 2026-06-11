using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using sk0ya.Loomo.Core.IO;

namespace sk0ya.Loomo.Core.Observability;

/// <summary>trace ファイル1件の概要（解析対象の選択用）。</summary>
public sealed record TraceFileInfo(string SessionId, DateTime UpdatedAt, long SizeBytes);

/// <summary>
/// <see cref="JsonlTraceSink"/> が書いた JSONL トレースを読み戻す（設計書 §20.5・Phase B）。
/// <para>
/// 書き込み側と同じ <see cref="JsonSerializerOptions"/>（CamelCase＋enum文字列）で
/// <see cref="TraceEvent"/> へ復元する。<c>Payload</c> は <c>object?</c> のため復元後は
/// <see cref="JsonElement"/> になり、<see cref="SessionMetrics"/> がフィールドを取り出す。
/// </para>
/// </summary>
public sealed class TraceReader
{
    // JsonlTraceSink.JsonOpts と同一設定（読み書きで揃える）。
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;

    public TraceReader() : this(JsonlTraceSink.DefaultDir()) { }

    public TraceReader(string dir) => _dir = dir;

    /// <summary>トレースファイルのディレクトリ（自動更新の監視用）。</summary>
    public string DirectoryPath => _dir;

    /// <summary>trace ディレクトリの jsonl ファイルを新しい順に列挙する。</summary>
    public IReadOnlyList<TraceFileInfo> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<TraceFileInfo>();

        var list = new List<TraceFileInfo>();
        foreach (var file in new DirectoryInfo(_dir).GetFiles("*.jsonl"))
        {
            var sessionId = Path.GetFileNameWithoutExtension(file.Name);
            list.Add(new TraceFileInfo(sessionId, file.LastWriteTime, file.Length));
        }
        return list.OrderByDescending(f => f.UpdatedAt).ToList();
    }

    /// <summary>指定セッションのトレースファイルを削除する（無ければ何もしない）。</summary>
    public void Delete(string sessionId)
    {
        try { File.Delete(PathFor(sessionId)); }
        catch (IOException) { /* ロック中などは無視 */ }
        catch (UnauthorizedAccessException) { /* 権限なしは無視 */ }
    }

    /// <summary>指定セッションのトレースイベントを順序どおりに読み込む。無ければ空。</summary>
    public IReadOnlyList<TraceEvent> Read(string sessionId)
    {
        var path = PathFor(sessionId);

        // 一括読みしてからパースする。ファイルを掴む時間を最小化し（追記側=JsonlTraceSink を妨げない）、
        // 読取中にローテーション(Rotate)で削除/ロックされても呼び出し側（UIスレッド）に例外を波及させない。
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException) { return Array.Empty<TraceEvent>(); }                 // 不在・削除・共有違反
        catch (UnauthorizedAccessException) { return Array.Empty<TraceEvent>(); }

        var events = new List<TraceEvent>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            TraceEvent? ev = null;
            try { ev = JsonSerializer.Deserialize<TraceEvent>(line, JsonOpts); }
            catch { /* 壊れた行はスキップ（追記中のクラッシュ等で末尾が欠ける場合がある） */ }
            if (ev is not null) events.Add(ev);
        }
        return events;
    }

    private string PathFor(string sessionId)
    {
        var safe = SafeFileName.Sanitize(sessionId);
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_dir, safe + ".jsonl");
    }
}
