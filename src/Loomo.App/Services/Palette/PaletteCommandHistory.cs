using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// コマンドパレットで実行した部屋の操作の履歴（§24.37）。開いただけの一覧の先頭に「最近使った」を出し、
/// よく使う操作にキーを覚えなくても 2 打で戻れるようにする（Zed v0.181.5 の永続化されたパレット履歴）。
/// ワークスペースをまたぐ道具の癖なので、アプリ単位の 1 ファイル（<c>%APPDATA%/Loomo/palette-history.json</c>）に持つ。
/// 鍵はコマンド ID、ID の無いコマンドは「分類＋名前」。
/// </summary>
public sealed class PaletteCommandHistory
{
    /// <summary>先頭に出す件数。</summary>
    public const int ShownCount = 5;

    /// <summary>覚えておく件数（使われなくなったものから落とす）。</summary>
    public const int MaxEntries = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _filePath;
    private readonly Func<DateTime> _now;
    private readonly object _saveGate = new();
    private int _saveVersion;
    private List<Entry> _entries;

    public sealed record Entry(string Key, DateTime LastUsedUtc, int Count);

    public PaletteCommandHistory(string? filePath, Func<DateTime>? now = null)
    {
        _filePath = filePath;
        _now = now ?? (() => DateTime.UtcNow);
        _entries = Load(filePath);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "palette-history.json");

    public IReadOnlyList<Entry> Entries => _entries;

    public static string KeyOf(PaletteCommand command)
        => string.IsNullOrEmpty(command.Id) ? $"{command.Category}\u001f{command.Title}" : command.Id;

    /// <summary>実行を記録する。場所を指す行（ファイル・シンボル等）は操作ではないので記録しない。</summary>
    public void Record(PaletteCommand command)
    {
        if (command.Target is not null) return;
        var key = KeyOf(command);
        var previous = _entries.FirstOrDefault(e => e.Key == key);
        _entries = _entries.Where(e => e.Key != key)
            .Prepend(new Entry(key, _now(), (previous?.Count ?? 0) + 1))
            .Take(MaxEntries)
            .ToList();
        Save();
    }

    /// <summary>記録を消す。</summary>
    public void Clear()
    {
        _entries = new List<Entry>();
        Save();
    }

    /// <summary>
    /// 一覧を「最近使った」順に組み替える：最近使ったもの（いま一覧にあるものだけ・新しい順に <see cref="ShownCount"/> 件）を
    /// 先頭へ寄せ、行の右に「最近」を添える（キーがあるものはキーのまま）。残りは元の順。
    /// 絞り込みは同じ強さの一致を元の並び順で並べるので、打ち始めてからも最近使ったものが同点の先頭に来る。
    /// </summary>
    public IReadOnlyList<PaletteCommand> Order(IReadOnlyList<PaletteCommand> commands)
    {
        if (_entries.Count == 0) return commands;
        var byKey = new Dictionary<string, PaletteCommand>(StringComparer.Ordinal);
        foreach (var command in commands)
            byKey.TryAdd(KeyOf(command), command);
        var recent = _entries
            .Select(e => byKey.GetValueOrDefault(e.Key))
            .Where(c => c is not null)
            .Cast<PaletteCommand>()
            .Take(ShownCount)
            .ToList();
        if (recent.Count == 0) return commands;
        var moved = new HashSet<PaletteCommand>(recent);
        return recent
            .Select(c => c.Shortcut is null && c.Badge is null ? c with { Badge = "最近" } : c)
            .Concat(commands.Where(c => !moved.Contains(c)))
            .ToList();
    }

    private void Save()
    {
        if (_filePath is null) return;
        var snapshot = _entries.ToList();
        var path = _filePath;
        var version = System.Threading.Interlocked.Increment(ref _saveVersion);
        // 実行のたびの書き込みは UI スレッドで待たない（小さいが、ディスクが遅いと打鍵の後ろで詰まる）。
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
                .Where(e => !string.IsNullOrEmpty(e.Key))
                .OrderByDescending(e => e.LastUsedUtc)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<Entry>();
        }
    }
}
