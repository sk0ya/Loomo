using System.Globalization;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary><see cref="LocalFileServer"/> が返す1件分の応答（WebView2 に依らない部分）。
/// ディスクを読むので UI スレッドでは組まない（§31.15）。</summary>
internal sealed record LocalFileResponse(int Status, string Reason, Stream Content, string Headers)
{
    /// <summary>これ以下のファイル（範囲）はメモリへ読んでから渡す。WebView2 に FileStream を渡すと、
    /// 読み終えても GC まで閉じられずハンドルが残る——小さいものはその場で閉じておく。</summary>
    internal const long BufferLimit = 8 * 1024 * 1024;

    /// <param name="requestUri">要求された URL。フォルダーを末尾 <c>/</c> 無しで頼まれたら、<c>/</c> 付きへ移す
    /// （そのまま index.html を返すと、ページの相対リンクが1つ上のフォルダーを基準に解けて全部外れる）。</param>
    public static LocalFileResponse Create(string path, string? range, string? requestUri = null)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (requestUri is not null && SlashedFolderUrl(requestUri) is { } slashed)
                    return Moved(slashed);
                var index = Path.Combine(path, "index.html");
                if (!File.Exists(index))
                    return Text(404, "Not Found", "フォルダーには index.html がありません: " + path);
                path = index;
            }
            if (!File.Exists(path))
                return Text(404, "Not Found", "ファイルが見つかりません: " + path);

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var type = ContentTypes.For(path);

            if (TryParseRange(range, length, out var start, out var end))
            {
                // 長すぎる範囲は手前で切る（206 は求めより短く返してよい。受け手は Content-Range で続きを取りに来る）。
                if (end - start + 1 > BufferLimit && end != length - 1)
                    end = start + BufferLimit - 1;
                var count = end - start + 1;
                stream.Seek(start, SeekOrigin.Begin);
                Stream content = count <= BufferLimit ? Buffer(stream, count) : stream;
                return new(206, "Partial Content", content, HeaderLines(type, count,
                    $"Content-Range: bytes {start}-{end}/{length}"));
            }
            // 範囲の無い要求と、受けない形の範囲（複数範囲・範囲外）は全体を返す。
            return new(200, "OK", length <= BufferLimit ? Buffer(stream, length) : (Stream)stream, HeaderLines(type, length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Text(403, "Forbidden", "ファイルを読めません: " + ex.Message);
        }
    }

    public static LocalFileResponse Forbidden()
        => Text(403, "Forbidden", "このページからはローカルファイルを読めません。");

    private static LocalFileResponse Moved(string location)
        => new(301, "Moved Permanently", Stream.Null,
            "Location: " + location + "\r\nContent-Length: 0\r\nCache-Control: no-store");

    public static LocalFileResponse NotFound()
        => Text(404, "Not Found", "ローカルファイルの URL として読めません。");

    /// <summary>パスが <c>/</c> で終わっていなければ、終わらせた URL（クエリ・フラグメントは保つ）。</summary>
    internal static string? SlashedFolderUrl(string requestUri)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) || uri.AbsolutePath.EndsWith('/'))
            return null;
        return new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri.AbsoluteUri;
    }

    private static bool IsBytesRange(string range)
        => range.TrimStart().StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>bytes=a-b</c>／<c>bytes=a-</c>／<c>bytes=-n</c> の単一範囲だけ受ける（複数範囲は全体を返す）。</summary>
    internal static bool TryParseRange(string? header, long length, out long start, out long end)
    {
        start = end = 0;
        if (header is null || !IsBytesRange(header) || length == 0)
            return false;
        var spec = header.Trim()[6..].Trim();
        if (spec.Contains(','))
            return false;
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        const NumberStyles Digits = NumberStyles.None;
        if (first.Length == 0)
        {
            if (!long.TryParse(last, Digits, CultureInfo.InvariantCulture, out var suffix) || suffix == 0)
                return false;
            start = Math.Max(0, length - suffix);
            end = length - 1;
            return true;
        }
        if (!long.TryParse(first, Digits, CultureInfo.InvariantCulture, out start) || start >= length)
            return false;
        if (last.Length == 0)
        {
            end = length - 1;
            return true;
        }
        if (!long.TryParse(last, Digits, CultureInfo.InvariantCulture, out end) || end < start)
            return false;
        end = Math.Min(end, length - 1);
        return true;
    }

    private static MemoryStream Buffer(FileStream stream, long count)
    {
        using (stream)
        {
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return new MemoryStream(bytes, writable: false);
        }
    }

    // 編集して読み直したら必ず新しい中身が出るよう、キャッシュさせない。
    private static string HeaderLines(string type, long length, string? extra = null)
        => $"Content-Type: {type}\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store"
           + (extra is null ? "" : "\r\n" + extra);

    private static LocalFileResponse Text(int status, string reason, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return new(status, reason, new MemoryStream(bytes, writable: false),
            HeaderLines("text/plain; charset=utf-8", bytes.Length));
    }

    private static class ContentTypes
    {
        // 文字コードは付けない——HTTP ヘッダーの charset は文書内の <meta charset> より強く、
        // Shift_JIS の古い HTML を UTF-8 と決めつけて化けさせてしまう。
        private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html", [".htm"] = "text/html", [".xhtml"] = "application/xhtml+xml",
            [".css"] = "text/css", [".js"] = "text/javascript", [".mjs"] = "text/javascript",
            [".cjs"] = "text/javascript", [".json"] = "application/json", [".map"] = "application/json",
            [".wasm"] = "application/wasm", [".xml"] = "application/xml", [".txt"] = "text/plain",
            [".md"] = "text/plain", [".csv"] = "text/csv",
            [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif", [".ico"] = "image/x-icon",
            [".bmp"] = "image/bmp",
            [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
            [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".ogv"] = "video/ogg", [".ogg"] = "audio/ogg",
            [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".m4a"] = "audio/mp4", [".flac"] = "audio/flac",
            [".pdf"] = "application/pdf",
        };

        public static string For(string path)
            => Map.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
    }
}
