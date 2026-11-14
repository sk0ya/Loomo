using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 差分の「見た」印の置き場（§24.107）。鍵は（見方・ファイルの絶対パス）、値は見たときのパッチの指紋（新しいものが後ろ・
/// <see cref="DiffViewedMarks.MaxFingerprintsPerFile"/> まで）。部屋を閉じても、Loomo を閉じても残す——大きな変更を途中まで読んで
/// 席を立ち、戻ったときにどこまで読んだかが分かることが目的なので。
///
/// <para>置き場所は <c>%APPDATA%/Loomo/diff-viewed.json</c> の 1 ファイル。<see cref="Retention"/> のあいだ触っていないものと、
/// <see cref="MaxEntries"/> を超えた古いものは起動のついでに刈る。</para>
///
/// <para><b>ディスクは UI スレッドで触らない</b>（§31.15）。読むのは生成時に背景で 1 度だけ、以後はメモリの表を引く。書くのは
/// <see cref="DeferredWriteQueue"/> の 1 本のスレッドで、続けざまの書き込みは最後の 1 回に畳まれる。Loomo を 2 つ開いていても
/// 互いの分を消さないよう、書く直前にファイルを読み直し、こちらが触っていない鍵は新しい方を取り込む（<see cref="EditorPositionStore"/> と同じ）。</para>
/// </summary>
public sealed class DiffViewedStore : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);
    public const int MaxEntries = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _file;
    private readonly DeferredWriteQueue? _queue;
    private readonly object _gate = new();
    private readonly Task _loading;
    private Dictionary<string, Entry>? _entries;
    private readonly List<Func<Dictionary<string, Entry>, bool>> _pending = [];
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private DateTime _loadedUtc;

    /// <param name="synchronous">テスト用。true なら読み込み・書き込みをその場で行う。</param>
    public DiffViewedStore(string file, bool synchronous = false)
    {
        _file = file;
        _queue = synchronous ? null : new DeferredWriteQueue("Loomo.DiffViewed");
        if (synchronous)
        {
            CompleteLoading();
            _loading = Task.CompletedTask;
        }
        else
        {
            _loading = Task.Run(CompleteLoading);
        }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "diff-viewed.json");

    /// <summary>鍵：見方と、正規化した絶対パス（Windows では大文字小文字を区別しない）。</summary>
    public static string Key(string scope, string path) => scope + "|" + Path.GetFullPath(path).ToUpperInvariant();

    /// <summary>覚えている指紋（古い順）。無ければ null。読み込みが済んでいなければ待つ（UI スレッドは塞がない）。</summary>
    public async Task<IReadOnlyList<string>?> GetAsync(string scope, string path)
    {
        await _loading.ConfigureAwait(false);
        return Get(scope, path);
    }

    /// <summary>読み込みが済んでいれば覚えている指紋。済んでいなければ null（印の無い一覧として扱う）。</summary>
    public IReadOnlyList<string>? Get(string scope, string path)
    {
        var key = Key(scope, path);
        lock (_gate)
            return _entries is not null && _entries.TryGetValue(key, out var entry) && entry.Fingerprints is { Count: > 0 } prints
                ? prints.ToArray()
                : null;
    }

    /// <summary>このパッチを見た。同じ指紋はいちばん新しい位置へ動かす。</summary>
    public void MarkViewed(string scope, string path, string fingerprint, DateTime? nowUtc = null)
    {
        var key = Key(scope, path);
        var now = nowUtc ?? DateTime.UtcNow;
        Mutate(entries =>
        {
            _touched.Add(key);
            var prints = entries.TryGetValue(key, out var entry) && entry.Fingerprints is { } existing ? existing : [];
            prints.Remove(fingerprint);
            prints.Add(fingerprint);
            while (prints.Count > DiffViewedMarks.MaxFingerprintsPerFile)
                prints.RemoveAt(0);
            entries[key] = new Entry { Fingerprints = prints, LastUsedUtc = now };
            TrimToMax(entries);
            return true;
        });
    }

    /// <summary>このファイルの印を外す（この見方で覚えていた指紋を全部忘れる）。</summary>
    public void Forget(string scope, string path)
    {
        var key = Key(scope, path);
        Mutate(entries =>
        {
            _touched.Add(key);
            return entries.Remove(key);
        });
    }

    public void Flush() => _queue?.Flush();

    public void Dispose()
    {
        _loading.Wait();
        _queue?.Dispose();
    }

    private void Mutate(Func<Dictionary<string, Entry>, bool> change)
    {
        lock (_gate)
        {
            if (_entries is null)
            {
                _pending.Add(change);
                return;
            }
            if (change(_entries))
                ScheduleWrite();
        }
    }

    private void CompleteLoading()
    {
        var entries = ReadFile();
        var before = entries.Count;
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var (key, entry) in entries.ToList())
            if (entry.LastUsedUtc < cutoff || entry.Fingerprints is not { Count: > 0 })
                entries.Remove(key);
        TrimToMax(entries);
        lock (_gate)
        {
            _loadedUtc = DateTime.UtcNow;
            _entries = entries;
            var changed = entries.Count != before;
            foreach (var change in _pending)
                changed |= change(entries);
            _pending.Clear();
            if (changed)
                ScheduleWrite();
        }
    }

    private Dictionary<string, Entry> ReadFile()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_file)
                && JsonSerializer.Deserialize<ViewedFile>(File.ReadAllText(_file, Encoding.UTF8), JsonOptions)?.Files is { } files)
            {
                foreach (var (key, entry) in files)
                    if (entry is not null)
                        entries[key] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[DiffViewed] 読めませんでした: {ex.Message}");
        }
        return entries;
    }

    private static void TrimToMax(Dictionary<string, Entry> entries)
    {
        if (entries.Count <= MaxEntries)
            return;
        foreach (var key in entries.OrderBy(pair => pair.Value.LastUsedUtc).Take(entries.Count - MaxEntries).Select(pair => pair.Key).ToList())
            entries.Remove(key);
    }

    /// <summary>呼ぶ側が <see cref="_gate"/> を持つ。</summary>
    private void ScheduleWrite()
    {
        var work = new WriteWork(this);
        if (_queue is null)
            work.Execute();
        else
            _queue.Enqueue(work);
    }

    private void WriteNow()
    {
        var disk = ReadFile();
        string json;
        lock (_gate)
        {
            var entries = _entries!;
            foreach (var (key, entry) in disk)
            {
                if (_touched.Contains(key) || entry.LastUsedUtc <= _loadedUtc)
                    continue;
                if (!entries.TryGetValue(key, out var mine) || mine.LastUsedUtc < entry.LastUsedUtc)
                    entries[key] = entry;
            }
            TrimToMax(entries);
            json = JsonSerializer.Serialize(new ViewedFile { Files = new(entries, StringComparer.Ordinal) }, JsonOptions);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class WriteWork(DiffViewedStore owner) : IDeferredWork
    {
        private readonly DiffViewedStore _owner = owner;

        public bool Covers(IDeferredWork older) => older is WriteWork other && ReferenceEquals(other._owner, _owner);

        public void Execute()
        {
            try { _owner.WriteNow(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[DiffViewed] 書けませんでした: {ex.Message}");
            }
        }
    }

    private sealed class ViewedFile
    {
        public Dictionary<string, Entry>? Files { get; set; }
    }

    private sealed class Entry
    {
        public List<string>? Fingerprints { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }
}
