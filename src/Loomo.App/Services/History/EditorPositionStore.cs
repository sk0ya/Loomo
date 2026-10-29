using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイルを手放したときのカーソル位置（0 始まり）と縦スクロール位置（0..1）。</summary>
public sealed record EditorPosition(int Line, int Column, double? ScrollRatio);

/// <summary>
/// タブを閉じたファイルのカーソル位置の置き場（Vim の <c>'"</c> マーク＝viminfo に当たるもの）。次にそのファイルを
/// 開いたとき、前回いた行へ戻す。ワークスペースの復元（開いたままのタブ）と棚（§24.29）は自前で位置を持つので、
/// ここが受け持つのは「閉じて、あとで別の道から開き直した」ときだけ。
///
/// <para>置き場所は <c>%APPDATA%/Loomo/editor-positions.json</c> の 1 ファイル。1 件が数十バイトなので
/// undo（§24.96）のようにファイルを分けない。<b>使わなくなったものは起動のついでに刈る</b>——
/// <see cref="Retention"/> のあいだ閉じられていない（＝開かれていない）もの・もう無いファイル・
/// <see cref="MaxEntries"/> を超えた古いもの。先頭（0 行 0 列・スクロール 0）へ戻ったファイルは覚えておく理由が
/// 無いので、その場で消す。</para>
///
/// <para><b>ディスクは UI スレッドで触らない</b>（§31.15）。読むのは生成時に背景で 1 度だけ、以後はメモリの表を
/// 引く。書くのは <see cref="DeferredWriteQueue"/> の 1 本のスレッドで、続けざまの書き込みは最後の 1 回に畳まれる。
/// 表の書き出し（直列化）も書き出しスレッドで行うので、積んだ順と書かれる中身の新しさが入れ替わらない。</para>
///
/// <para>読み込みが済む前に来た変更は捨てずに積み、読み込みが済んだ時点で来た順に当てる。</para>
///
/// <para>Loomo を 2 つ開いていても互いの分を消さないよう、書く直前にファイルを読み直し、読み込み後に相手が書いた
/// 項目（こちらが触っていないもの）を取り込んでから書く。</para>
/// </summary>
public sealed class EditorPositionStore : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public const int MaxEntries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _file;
    private readonly DeferredWriteQueue? _queue;
    private readonly Func<string, bool> _fileExists;
    private readonly object _gate = new();
    private readonly Task _loading;

    /// <summary>メモリの表。<see cref="_loading"/> が済むまでは null（その間の変更は <see cref="_pending"/> へ）。</summary>
    private Dictionary<string, Entry>? _entries;
    private readonly List<Func<Dictionary<string, Entry>, bool>> _pending = [];

    /// <summary>このプロセスが書き換えた（覚えた・忘れた・付け替えた）鍵。他プロセスの分を取り込むとき、これは上書きしない。</summary>
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private DateTime _loadedUtc;

    /// <param name="synchronous">テスト用。true なら読み込み・書き込みをその場で行う。</param>
    /// <param name="fileExists">刈るときの「ファイルがまだあるか」。テスト用の差し替え口（既定は <see cref="File.Exists(string)"/>）。</param>
    public EditorPositionStore(string file, bool synchronous = false, Func<string, bool>? fileExists = null)
    {
        _file = file;
        _fileExists = fileExists ?? File.Exists;
        _queue = synchronous ? null : new DeferredWriteQueue("Loomo.EditorPositions");
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "editor-positions.json");

    /// <summary>鍵：正規化した絶対パス（Windows では大文字小文字を区別しない）。</summary>
    public static string DocumentId(string path) => Path.GetFullPath(path).ToUpperInvariant();

    /// <summary>前に覚えた位置。無ければ null。読み込みが済んでいなければ済むのを待つ（UI スレッドを塞がない）。</summary>
    public async Task<EditorPosition?> GetAsync(string path)
    {
        await _loading.ConfigureAwait(false);
        var key = DocumentId(path);
        lock (_gate)
            return _entries!.TryGetValue(key, out var entry) ? new EditorPosition(entry.Line, entry.Column, entry.ScrollRatio) : null;
    }

    /// <summary>このファイルを手放したときの位置を覚える。先頭なら忘れる。</summary>
    public void Remember(string path, EditorPosition position, DateTime? nowUtc = null)
        => RememberMany([(path, position)], nowUtc);

    /// <summary>まとめて覚える（ウィンドウを閉じるとき）。書き出しは 1 回。</summary>
    public void RememberMany(IEnumerable<(string Path, EditorPosition Position)> items, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var changes = items.Select(item => (Key: DocumentId(item.Path), item.Position)).ToList();
        if (changes.Count == 0)
            return;
        Mutate(entries =>
        {
            var changed = false;
            foreach (var (key, position) in changes)
            {
                _touched.Add(key);
                if (IsTop(position))
                {
                    changed |= entries.Remove(key);
                    continue;
                }
                entries[key] = new Entry
                {
                    Line = Math.Max(0, position.Line),
                    Column = Math.Max(0, position.Column),
                    ScrollRatio = position.ScrollRatio is { } ratio && double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : null,
                    LastUsedUtc = now,
                };
                changed = true;
            }
            TrimToMax(entries);
            return changed;
        });
    }

    /// <summary>名前を変えたファイル（フォルダーなら、その下の全部）の位置を新しい名前へ付け替える。</summary>
    public void Move(string oldPath, string newPath, bool isDirectory)
    {
        var oldId = DocumentId(oldPath).TrimEnd(Path.DirectorySeparatorChar);
        var newId = DocumentId(newPath).TrimEnd(Path.DirectorySeparatorChar);
        Mutate(entries =>
        {
            var moved = entries.Keys
                .Where(key => isDirectory
                    ? key.StartsWith(oldId + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    : string.Equals(key, oldId, StringComparison.Ordinal))
                .ToList();
            foreach (var key in moved)
            {
                var target = newId + key[oldId.Length..];
                entries[target] = entries[key];
                entries.Remove(key);
                _touched.Add(key);
                _touched.Add(target);
            }
            return moved.Count > 0;
        });
    }

    public void Flush() => _queue?.Flush();

    /// <summary>読み込み待ちに積んだ変更を当て終えてから、書き込みを書き切る。</summary>
    public void Dispose()
    {
        _loading.Wait();
        _queue?.Dispose();
    }

    /// <summary>表を変える唯一の入口。<paramref name="change"/> は表が変わったら true を返す。</summary>
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
        var (entries, pruned) = LoadAndPrune();
        lock (_gate)
        {
            _loadedUtc = DateTime.UtcNow;
            _entries = entries;
            var changed = pruned;
            foreach (var change in _pending)
                changed |= change(entries);
            _pending.Clear();
            if (changed)
                ScheduleWrite();
        }
    }

    private static bool IsTop(EditorPosition position)
        => position.Line <= 0 && position.Column <= 0 && (position.ScrollRatio is not { } ratio || ratio <= 0);

    private (Dictionary<string, Entry> Entries, bool Pruned) LoadAndPrune()
    {
        var entries = ReadFile();
        var before = entries.Count;
        try
        {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var (key, entry) in entries.ToList())
                if (entry.LastUsedUtc < cutoff || !Exists(key))
                    entries.Remove(key);
            TrimToMax(entries);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPositions] 刈れませんでした: {ex.Message}");
        }
        return (entries, entries.Count != before);
    }

    /// <summary>置き場を読む。無い・読めない・壊れているなら空（覚えていないのと同じ扱い。次の書き込みで作り直す）。
    /// ここで失敗しても、ファイルを開く・閉じる側を巻き込まない。</summary>
    private Dictionary<string, Entry> ReadFile()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_file)
                && JsonSerializer.Deserialize<PositionFile>(File.ReadAllText(_file, Encoding.UTF8), JsonOptions)?.Files is { } files)
            {
                foreach (var (key, entry) in files)
                    if (entry is not null)
                        entries[key] = entry;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPositions] 読めませんでした: {ex.Message}");
        }
        return entries;
    }

    private bool Exists(string key)
    {
        try { return _fileExists(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>上限を超えた分を、いちばん長く使っていないものから落とす。</summary>
    private static void TrimToMax(Dictionary<string, Entry> entries)
    {
        if (entries.Count <= MaxEntries)
            return;
        foreach (var key in entries.OrderBy(pair => pair.Value.LastUsedUtc).Take(entries.Count - MaxEntries).Select(pair => pair.Key).ToList())
            entries.Remove(key);
    }

    /// <summary>書き込みを積む。呼ぶ側が <see cref="_gate"/> を持つ。</summary>
    private void ScheduleWrite()
    {
        var work = new WriteWork(this);
        if (_queue is null)
            work.Execute();
        else
            _queue.Enqueue(work);
    }

    /// <summary>書き出しスレッドで：他プロセスの分を取り込み、その時点の表を書く。</summary>
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
                    continue;   // こちらが触った鍵・読み込んだ時点で既にあったもの（＝刈った・忘れたもの）は戻さない
                if (!entries.TryGetValue(key, out var mine) || mine.LastUsedUtc < entry.LastUsedUtc)
                    entries[key] = entry;
            }
            TrimToMax(entries);
            json = JsonSerializer.Serialize(new PositionFile { Files = new(entries, StringComparer.Ordinal) }, JsonOptions);
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

    /// <summary>書き込み 1 回。中身は実行時に作るので、まだ書いていない前の回は捨ててよい。</summary>
    private sealed class WriteWork(EditorPositionStore owner) : IDeferredWork
    {
        public bool Covers(IDeferredWork older) => older is WriteWork other && ReferenceEquals(other._owner, _owner);

        private readonly EditorPositionStore _owner = owner;

        public void Execute()
        {
            try { _owner.WriteNow(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[EditorPositions] 書けませんでした: {ex.Message}");
            }
        }
    }

    private sealed class PositionFile
    {
        public Dictionary<string, Entry>? Files { get; set; }
    }

    private sealed class Entry
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public double? ScrollRatio { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }
}
