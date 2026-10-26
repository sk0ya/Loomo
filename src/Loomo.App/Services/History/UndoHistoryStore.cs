using System.Security.Cryptography;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// エディタの取り消し履歴の置き場（Vim の 'undofile' に当たるもの・§24.96）。タブを閉じても・Loomo を閉じても、
/// 次にそのファイルを開いたときに <c>u</c> で戻れるようにする。中身の出し入れと「同じ文書の同じ本文か」の確かめは
/// エディタ（<c>VimEditorControl.ExportUndoHistory</c>／<c>ImportUndoHistory</c>）が行い、ここは置くだけ。
///
/// <para>置き場所は <c>%APPDATA%/Loomo/undo/&lt;パスのハッシュ&gt;.json</c>。<see cref="Retention"/> より古いものと、
/// <see cref="MaxFiles"/> を超えた古いものは起動のついでに刈る。<b>書き込みは UI スレッドで行わない</b>（§31.15）——
/// 書き出しは <see cref="DeferredWriteQueue"/> へ積み、読み出しは背景で行ってから UI スレッドで入れる。</para>
/// </summary>
public sealed class UndoHistoryStore : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int MaxFiles = 300;

    /// <summary>1 ファイルの履歴に入れる文字数の上限（取り消しの 1 段は本文まるごとの写しなので、大きなファイルほど少なく入る）。</summary>
    public const long MaxCharacters = 8L * 1024 * 1024;

    private readonly string _root;
    private readonly DeferredWriteQueue? _queue;

    /// <param name="synchronous">テスト用。true なら積まずにその場で書く。</param>
    public UndoHistoryStore(string root, bool synchronous = false)
    {
        _root = root;
        _queue = synchronous ? null : new DeferredWriteQueue("Loomo.UndoHistory");
        Enqueue(Prune);
    }

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "undo");

    /// <summary>文書の鍵：正規化した絶対パス（Windows では大文字小文字を区別しない）。</summary>
    public static string DocumentId(string path) => Path.GetFullPath(path).ToUpperInvariant();

    /// <summary>履歴を書く（後で書き出しスレッドが書く）。</summary>
    public void Save(string path, string json)
    {
        var target = FileFor(path);
        Enqueue(() =>
        {
            Directory.CreateDirectory(_root);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        });
    }

    /// <summary>履歴を読む（積んだ書き込みを書き終えてから・呼ぶ側は背景スレッドで）。無ければ null。</summary>
    public string? Load(string path)
    {
        Flush();
        var file = FileFor(path);
        try
        {
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Flush() => _queue?.Flush();

    public void Dispose() => _queue?.Dispose();

    internal string FileFor(string path)
        => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DocumentId(path))))[..32] + ".json");

    private void Prune()
    {
        if (!Directory.Exists(_root)) return;
        var files = new DirectoryInfo(_root).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var (file, index) in files.Select((f, i) => (f, i)))
            if (index >= MaxFiles || file.LastWriteTimeUtc < cutoff)
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Enqueue(Action work)
    {
        if (_queue is null)
        {
            try { work(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
        _queue.Enqueue(() =>
        {
            try { work(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }
}
