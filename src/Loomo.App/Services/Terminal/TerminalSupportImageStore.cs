using System.IO;
using System.Security.Cryptography;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ターミナルサポートのページ（§39）に画像を出すための置き場（§39.10）。ページは一時フォルダーを仮想ホスト
/// （<see cref="MarkdownRenderer.PageVirtualHost"/>）として読むので、画像もその下の <c>tsimg/</c> に置いて同じホストの
/// URL で指す。本文は数秒おきに差し替わるので、<b>data: URI にはしない</b>——数 MB の base64 を毎回メッセージで流すことになる。
/// <para>ファイル名は中身（または出どころ）の指紋なので、同じ画像は1回しか書かない。書くのは描く側の裏のスレッドから。
/// 古いものは起動ごとに片付ける（数日より前のもの）。</para>
/// </summary>
internal sealed class TerminalSupportImageStore
{
    public const string FolderName = "tsimg";

    /// <summary>ファイルを写す上限。出力に名前が出てきただけの巨大な画像は縮小画像の元にしない。</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);
    private readonly string _folder;
    private readonly object _gate = new();

    public TerminalSupportImageStore(string previewFolder)
    {
        _folder = Path.Combine(previewFolder, FolderName);
    }

    /// <summary>古い画像を片付ける。消せなくても構わない。</summary>
    public void CleanStale()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return;
            foreach (var file in Directory.EnumerateFiles(_folder))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepFor)
                        File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Claude の記録の画像（§39.10.1）。記録の場所と行・何枚目で名前を決め、無ければ記録から引き直して書く。</summary>
    public string? ForTranscript(ClaudeImage image)
        => Ensure(Hash($"{image.TranscriptPath}|{image.Line}|{image.Index}"), Extension(image.MediaType),
            () => ClaudeTranscriptLookup.ReadImage(image));

    /// <summary>ディスクの画像ファイル（出力に名前が出てきたもの、§39.10.2）。場所・大きさ・更新時刻で名前を決めて写す。
    /// 無い・大きすぎる・画像でなければ null。</summary>
    public string? ForFile(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!TerminalOutputImages.IsImageExtension(extension))
            return null;
        return Ensure(Hash($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"), extension, () =>
        {
            try { return File.ReadAllBytes(info.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        });
    }

    /// <summary>端末に届いたインライン画像（§39.10.3）。中身の指紋で名前を決める。</summary>
    public string? ForBytes(byte[] data, string mimeType)
        => data.Length == 0 ? null : Ensure(Convert.ToHexString(SHA256.HashData(data))[..32], Extension(mimeType), () => data);

    private string? Ensure(string key, string extension, Func<byte[]?> read)
    {
        var name = key + extension;
        var path = Path.Combine(_folder, name);
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    if (read() is not { Length: > 0 } bytes)
                        return null;
                    Directory.CreateDirectory(_folder);
                    // 書きかけを読ませない：一時名で書いてから名前を付ける。
                    var temp = path + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, path, overwrite: true);
                }
                else
                {
                    // 使っているものは片付けの対象から外す。
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return $"https://{MarkdownRenderer.PageVirtualHost}/{FolderName}/{name}";
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32];

    internal static string Extension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".png",
    };
}
