using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 出自（§43）の項目を押した回数を日別に数える記録。§43.6 の「測り直し」のための記録点で、
/// 入れてから 2 週間後に「使われているか」を数字で見るために置く（機能そのものの動きには関わらない）。
/// ワークスペースをまたぐ使い方の記録なので、アプリ単位の 1 ファイル（<c>%APPDATA%/Loomo/provenance-usage.json</c>）に持つ。
/// 鍵は押した項目の名前：<c>request.show</c>（頼みごとの本文を出す）・<c>request.places</c>（この頼みごとで変わった場所）・
/// <c>request.copy</c>（全文のコピー）・<c>request.resume</c>（会話の再開）・<c>request.diff</c>・<c>places.jump</c>（場所へ跳ぶ）。
/// 行は「鍵 × 日（ローカルの日付）」で 1 つ。<see cref="MaxDays"/> より古い日の行は保存時に落とす。
/// </summary>
public sealed class ProvenanceUsageLog
{
    /// <summary>覚えておく日数。これより古い日の行は保存時に落とす。</summary>
    public const int MaxDays = 120;

    private const string DayFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _filePath;
    private readonly Func<DateTime> _now;
    private readonly object _saveGate = new();
    private int _saveVersion;
    private List<Entry> _entries;

    /// <summary>ある日にある項目を押した回数。<paramref name="Day"/> は "yyyy-MM-dd"（ローカルの日付）。</summary>
    public sealed record Entry(string Key, string Day, int Count);

    /// <param name="filePath">保存先。null なら保存しない（テスト用）。</param>
    /// <param name="now">ローカル時刻。既定は <see cref="DateTime.Now"/>。</param>
    public ProvenanceUsageLog(string? filePath, Func<DateTime>? now = null)
    {
        _filePath = filePath;
        _now = now ?? (() => DateTime.Now);
        _entries = Load(filePath);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "provenance-usage.json");

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>今日のその鍵の回数を 1 増やす（無ければ行を足す）。空の鍵は無視する。保存は背景で行う。</summary>
    public void Record(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var today = _now().Date;
        var day = DayOf(today);
        var oldest = DayOf(today.AddDays(-MaxDays));
        var previous = _entries.FirstOrDefault(e => e.Key == key && e.Day == day);
        var next = new List<Entry>(_entries.Count + 1);
        foreach (var entry in _entries)
        {
            // "yyyy-MM-dd" は文字列の順がそのまま日付の順。
            if (string.CompareOrdinal(entry.Day, oldest) < 0) continue;
            next.Add(ReferenceEquals(entry, previous) ? entry with { Count = entry.Count + 1 } : entry);
        }
        if (previous is null) next.Add(new Entry(key, day, 1));
        _entries = next;
        Save();
    }

    /// <summary>その鍵の全日の合計。</summary>
    public int CountOf(string key) => _entries.Where(e => e.Key == key).Sum(e => e.Count);

    private static string DayOf(DateTime date) => date.ToString(DayFormat, CultureInfo.InvariantCulture);

    private void Save()
    {
        if (_filePath is null) return;
        var snapshot = _entries.ToList();
        var path = _filePath;
        var version = System.Threading.Interlocked.Increment(ref _saveVersion);
        // 押すたびの書き込みは UI スレッドで待たない。
        // 続けて走った書き込みが追い越して古い中身で上書きしないよう、最新の版だけを書く。
        _ = Task.Run(() =>
        {
            lock (_saveGate)
            {
                if (version != System.Threading.Volatile.Read(ref _saveVersion)) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
    }

    private static List<Entry> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new List<Entry>();
        try
        {
            return (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), JsonOptions) ?? new List<Entry>())
                .Where(e => e is not null && !string.IsNullOrEmpty(e.Key) && IsDay(e.Day) && e.Count > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<Entry>();
        }
    }

    private static bool IsDay(string? day)
        => DateTime.TryParseExact(day, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
