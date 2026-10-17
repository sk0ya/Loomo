using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>ローカル履歴の 1 版が、いつ・なぜ残ったか。</summary>
public enum LocalHistoryKind
{
    /// <summary>エディタで開いた時点のディスクの中身（編集前に戻れる起点）。</summary>
    Opened,
    /// <summary>保存した中身。</summary>
    Saved,
    /// <summary>履歴から戻す直前のエディタの中身（戻すこと自体を取り消せるように）。</summary>
    BeforeRestore,
    /// <summary>文字コード・改行を変換する直前のディスクの中身（§23.3.4）。</summary>
    BeforeConvert,
}

/// <summary>ローカル履歴の 1 版。<see cref="StoragePath"/> は圧縮した中身の置き場所。</summary>
public sealed record LocalHistoryEntry(string FilePath, DateTime TimestampUtc, LocalHistoryKind Kind, string StoragePath)
{
    public string KindLabel => Kind switch
    {
        LocalHistoryKind.Opened => "開いた時点",
        LocalHistoryKind.Saved => "保存",
        LocalHistoryKind.BeforeRestore => "履歴から戻す前",
        LocalHistoryKind.BeforeConvert => "変換する前",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// ローカル履歴（Rider の Local History・設計書 §36）。ファイルの中身を時点ごとに残し、Git に入れる前に
/// 壊したファイルでも「その時点」へ戻せるようにする。軌跡（§27）は<b>どこへ行ったか</b>の記録で、こちらは
/// <b>中身がどう変わったか</b>の記録。
///
/// <para>置き場所は <c>%APPDATA%/Loomo/history/&lt;パスのハッシュ&gt;/</c>。1 版 1 ファイル（gzip）、名前は
/// <c>yyyyMMddTHHmmssfffZ_種類.gz</c>。元のパスは同じフォルダーの <c>path.txt</c>。直前の版と同じ中身は
/// 積まない。<see cref="Retention"/> より古い版と、1 ファイル <see cref="MaxEntriesPerFile"/> 版を超えた古い版は
/// 書き込みのついでに刈る。</para>
///
/// <para><b>書き込みは UI スレッドで行わない</b>（§31.15）。記録は <see cref="DeferredWriteQueue"/> へ積み、
/// 読み出し（一覧・中身）は積んだものを書き終えてから行う（read-after-write を守る）。</para>
/// </summary>
public sealed class LocalHistoryStore : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    public const int MaxEntriesPerFile = 200;
    /// <summary>これより大きいファイルは残さない（生成物・ログを抱え込まない）。</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private const string PathFileName = "path.txt";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly string _root;
    private readonly Func<DateTime> _utcNow;
    private readonly DeferredWriteQueue? _queue;

    /// <param name="synchronous">テスト用。true なら積まずにその場で書く。</param>
    public LocalHistoryStore(string root, Func<DateTime>? utcNow = null, bool synchronous = false)
    {
        _root = root;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _queue = synchronous ? null : new DeferredWriteQueue("Loomo.LocalHistory");
    }

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "history");

    /// <summary>エディタで開いたファイルの、いまのディスクの中身を起点として残す（直前の版と同じなら何もしない）。
    /// 読むのも書き込みスレッド側。</summary>
    public void RecordOpened(string? path)
    {
        if (!ShouldTrack(path)) return;
        var full = Path.GetFullPath(path!);
        var at = _utcNow();
        Enqueue(() =>
        {
            if (TryReadDisk(full) is { } text)
                Write(full, text, LocalHistoryKind.Opened, at);
        });
    }

    /// <summary>中身を 1 版として残す。<paramref name="text"/> は呼び出し側が取った写し。
    /// 改行は LF に揃えて残す——ディスク（CRLF）とエディタの本文（LF で結合）で同じ中身が別の版に見えないように。</summary>
    public void RecordContent(string? path, string? text, LocalHistoryKind kind)
    {
        if (!ShouldTrack(path) || text is null) return;
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes || text.Contains('\0')) return;
        var full = Path.GetFullPath(path!);
        var at = _utcNow();
        Enqueue(() => Write(full, text, kind, at));
    }

    /// <summary>そのファイルの版（新しい順）。</summary>
    public IReadOnlyList<LocalHistoryEntry> GetEntries(string path)
    {
        Flush();
        var full = Path.GetFullPath(path);
        return ReadEntries(FolderFor(full), full);
    }

    /// <summary>全ファイル横断の版（新しい順・最大 <paramref name="max"/> 件）。削除済みファイルも含む。</summary>
    public IReadOnlyList<LocalHistoryEntry> GetRecent(int max = 300)
    {
        Flush();
        if (!Directory.Exists(_root)) return [];
        var all = new List<LocalHistoryEntry>();
        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            if (ReadOriginalPath(folder) is not { } original) continue;
            all.AddRange(ReadEntries(folder, original));
        }
        return all.OrderByDescending(e => e.TimestampUtc).Take(max).ToList();
    }

    /// <summary>今はディスクに無いが履歴が残っているファイル（新しい順・最後の版つき）。</summary>
    public IReadOnlyList<LocalHistoryEntry> GetDeletedFiles()
        => GetRecent(int.MaxValue)
            .GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(e => !File.Exists(e.FilePath))
            .ToList();

    public string ReadContent(LocalHistoryEntry entry)
    {
        Flush();
        return Decompress(entry.StoragePath);
    }

    public void Flush() => _queue?.Flush();

    public void Dispose() => _queue?.Dispose();

    // ── 内部 ────────────────────────────────────────────────────────────────

    private static bool ShouldTrack(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && !MetadataSourceFiles.IsMetadataAsSource(path);

    private void Enqueue(Action work)
    {
        if (_queue is null) work();
        else _queue.Enqueue(work);
    }

    private static string? TryReadDisk(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            var text = File.ReadAllText(path);
            return text.Contains('\0') ? null : text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void Write(string fullPath, string text, LocalHistoryKind kind, DateTime atUtc)
    {
        text = text.Replace("\r\n", "\n");
        try
        {
            var folder = FolderFor(fullPath);
            Directory.CreateDirectory(folder);
            var pathFile = Path.Combine(folder, PathFileName);
            if (!File.Exists(pathFile))
                File.WriteAllText(pathFile, fullPath);

            var existing = ReadEntries(folder, fullPath);
            if (existing.Count > 0 && TryDecompress(existing[0].StoragePath) is { } latest &&
                latest.Replace("\r\n", "\n") == text)
                return;   // 直前の版と同じ中身は積まない（保存を連打しても版が増えない）

            var name = UniqueName(folder, atUtc, kind);
            using (var file = File.Create(Path.Combine(folder, name)))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                gzip.Write(bytes, 0, bytes.Length);
            }
            Prune(folder, fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            /* 履歴は補助。書けなくても編集・保存は止めない */
        }
    }

    /// <summary>版の中身。壊れた版（書き込み途中で落ちた gzip）は消して null——残すと最新の版として
    /// 毎回の比較で例外になり、そのファイルの履歴が以後ずっと残らなくなる。</summary>
    private static string? TryDecompress(string storagePath)
    {
        try { return Decompress(storagePath); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            try { File.Delete(storagePath); } catch (IOException) { }
            return null;
        }
    }

    private static string UniqueName(string folder, DateTime atUtc, LocalHistoryKind kind)
    {
        var stamp = atUtc;
        while (true)
        {
            var name = $"{stamp.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture)}_{kind}.gz";
            if (!File.Exists(Path.Combine(folder, name))) return name;
            stamp = stamp.AddMilliseconds(1);
        }
    }

    private void Prune(string folder, string fullPath)
    {
        var entries = ReadEntries(folder, fullPath);
        var cutoff = _utcNow() - Retention;
        for (var i = 0; i < entries.Count; i++)
        {
            // 最新の 1 版は古くても残す（いまの中身に最も近い起点）。
            if (i == 0) continue;
            if (i >= MaxEntriesPerFile || entries[i].TimestampUtc < cutoff)
            {
                try { File.Delete(entries[i].StoragePath); }
                catch (IOException) { }
            }
        }
    }

    private static List<LocalHistoryEntry> ReadEntries(string folder, string fullPath)
    {
        if (!Directory.Exists(folder)) return [];
        var entries = new List<LocalHistoryEntry>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.gz"))
            if (TryParseName(Path.GetFileName(file), out var at, out var kind))
                entries.Add(new LocalHistoryEntry(fullPath, at, kind, file));
        entries.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        return entries;
    }

    internal static bool TryParseName(string name, out DateTime atUtc, out LocalHistoryKind kind)
    {
        atUtc = default;
        kind = default;
        if (!name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^3];
        var underscore = stem.IndexOf('_');
        if (underscore < 0) return false;
        return DateTime.TryParseExact(stem[..underscore], TimestampFormat,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                   out atUtc)
               && Enum.TryParse(stem[(underscore + 1)..], out kind);
    }

    private static string? ReadOriginalPath(string folder)
    {
        try
        {
            var file = Path.Combine(folder, PathFileName);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException) { return null; }
    }

    private static string Decompress(string storagePath)
    {
        using var file = File.OpenRead(storagePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string FolderFor(string fullPath)
    {
        var key = fullPath.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_root, Convert.ToHexString(hash, 0, 8));
    }
}
