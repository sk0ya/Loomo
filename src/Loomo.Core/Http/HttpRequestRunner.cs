using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace sk0ya.Loomo.Core.Http;

/// <summary>変数を解決し終えた、送る直前のリクエスト。<see cref="Error"/> があれば送らない。</summary>
public sealed record PreparedHttpRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    IReadOnlyCollection<string> Unresolved,
    string? Error);

/// <summary>1往復の結果。送れなかった（名前解決・接続・タイムアウト）ときは <see cref="StatusCode"/> が null で <see cref="Error"/> に理由。</summary>
public sealed record HttpExchangeResult(
    PreparedHttpRequest Request,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    int? StatusCode,
    string? ReasonPhrase,
    string? HttpVersion,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    long BodyBytes,
    string BodyText,
    string DisplayBody,
    bool IsBinary,
    bool IsTruncated,
    string? Error)
{
    public bool IsSuccess => StatusCode is >= 200 and < 400;

    /// <summary>応答の1行目（「200 OK」）。送れなかったら「送れませんでした」。</summary>
    public string StatusLine => StatusCode is { } code
        ? $"{code} {ReasonPhrase}".TrimEnd()
        : "送れませんでした";

    /// <summary>名前付きリクエストの応答として後から引ける形（本文は整形前の生のもの）。</summary>
    public HttpNamedResponse ToNamedResponse() => new(Headers, BodyText);
}

/// <summary>
/// .http のリクエストを組み立てて送る（§24.30）。<b>送るのは人が ▶ を押したときだけ</b>——ファイルを開いた・
/// 編集しただけで送らない（.http には POST も DELETE も書かれている）。
/// <para>
/// HttpClient はこの実体が1本持ち、クッキーは実体の寿命の間（＝ Loomo の起動中）共有する。ログインの応答で
/// 受け取ったクッキーで次のリクエストを送れるように（REST Client と同じ振る舞い）。
/// </para>
/// </summary>
public sealed class HttpRequestRunner : IDisposable
{
    /// <summary>表示する本文の上限。それより大きい応答は切り詰めて、その旨を出す（全文はエディタで開く）。</summary>
    public const int MaxDisplayChars = 1_000_000;

    /// <summary>読み込む本文の上限。</summary>
    public const long MaxBodyBytes = 32L * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        // 日本語・記号を \uXXXX にしない（読むための表示）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _client;

    public HttpRequestRunner(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,   // 打ち切りは SendAsync の CancellationToken で（取消ボタンと同じ道）
        };
    }

    /// <summary>変数を解決して送る直前の形にする。<paramref name="baseDirectory"/> は <c>&lt; ./body.json</c> の起点（.http のフォルダー）。</summary>
    public static PreparedHttpRequest Prepare(HttpRequestBlock block, HttpVariableResolver resolver, string? baseDirectory)
    {
        var url = resolver.Resolve(block.Url).Trim();
        var headers = block.Headers
            .Select(h => new KeyValuePair<string, string>(resolver.Resolve(h.Key), resolver.Resolve(h.Value)))
            .ToList();
        string? body = null;
        string? error = null;
        if (block.BodyFile is { } bodyFile)
        {
            var path = resolver.Resolve(bodyFile);
            if (!Path.IsPathRooted(path) && baseDirectory is not null)
                path = Path.GetFullPath(Path.Combine(baseDirectory, path));
            try { body = resolver.Resolve(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"本文のファイルを読めません: {path}（{ex.Message}）";
            }
        }
        else if (block.Body is { } text)
        {
            body = resolver.Resolve(text);
        }

        if (error is null)
        {
            if (url.Length == 0)
                error = "URL がありません";
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                error = resolver.Unresolved.Count > 0
                    ? $"URL を組み立てられません（未定義の変数: {string.Join(", ", resolver.Unresolved)}）"
                    : $"URL として読めません: {url}";
        }
        return new PreparedHttpRequest(block.Method, url, headers, body, resolver.Unresolved.ToList(), error);
    }

    public async Task<HttpExchangeResult> SendAsync(PreparedHttpRequest request, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        if (request.Error is { } prepareError)
            return Failed(request, startedAt, watch.Elapsed, prepareError);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            using var message = BuildMessage(request);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var (bytes, truncatedBytes) = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            watch.Stop();

            var headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var isBinary = IsBinary(contentType, bytes);
            var text = isBinary ? "" : Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            var display = isBinary
                ? $"（バイナリ {FormatSize(bytes.LongLength)}・{contentType ?? "種類不明"}）"
                : Pretty(text, contentType);
            var truncated = truncatedBytes;
            if (display.Length > MaxDisplayChars)
            {
                display = display[..MaxDisplayChars];
                truncated = true;
            }
            return new HttpExchangeResult(request, startedAt, watch.Elapsed,
                (int)response.StatusCode, response.ReasonPhrase, $"HTTP/{response.Version}",
                headers, contentType, bytes.LongLength, text, display, isBinary, truncated, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failed(request, startedAt, watch.Elapsed, "取り消しました");
        }
        catch (OperationCanceledException)
        {
            return Failed(request, startedAt, watch.Elapsed, $"{DefaultTimeout.TotalSeconds:0} 秒で応答がありませんでした");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or FormatException or NotSupportedException or IOException)
        {
            return Failed(request, startedAt, watch.Elapsed, Describe(ex));
        }
    }

    private static HttpRequestMessage BuildMessage(PreparedHttpRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        var contentHeaders = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
                contentHeaders.Add(new(name, value));
        }
        if (request.Body is { } body)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            foreach (var (name, value) in contentHeaders)
                content.Headers.TryAddWithoutValidation(name, value);
            message.Content = content;
        }
        return message;
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            var room = MaxBodyBytes - buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, (int)room);
                return (buffer.ToArray(), true);
            }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    internal static bool IsBinary(string? mediaType, byte[] bytes)
    {
        if (mediaType is { } type)
        {
            if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || type.Contains("json", StringComparison.OrdinalIgnoreCase)
                || type.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || type.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || type.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || type.Contains("yaml", StringComparison.OrdinalIgnoreCase)
                || type.Contains("graphql", StringComparison.OrdinalIgnoreCase))
                return false;
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || type.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                || type.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                || type.Contains("zip", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // 種類の書かれていない応答は中身で見る（先頭に NUL があればバイナリ）
        var probe = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < probe; i++)
            if (bytes[i] == 0)
                return true;
        return false;
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
        }
        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>読むための整形。JSON（と見えるもの）は字下げし、それ以外はそのまま。</summary>
    public static string Pretty(string text, string? mediaType)
    {
        var looksJson = mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                        || text.TrimStart() is { Length: > 0 } t && (t[0] == '{' || t[0] == '[');
        if (!looksJson)
            return text;
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;
        return ReferenceEquals(inner, ex) ? ex.Message : $"{ex.Message}（{inner.Message}）";
    }

    private static HttpExchangeResult Failed(PreparedHttpRequest request, DateTimeOffset startedAt, TimeSpan elapsed, string error)
        => new(request, startedAt, elapsed, null, null, null, [], null, 0, "", "", false, false, error);

    public void Dispose() => _client.Dispose();
}
