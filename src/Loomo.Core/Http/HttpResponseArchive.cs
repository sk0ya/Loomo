using System.Text.Encodings.Web;
using System.Text.Json;

namespace sk0ya.Loomo.Core.Http;

/// <summary>
/// .http の「各リクエストの最後の応答」を、再起動をまたいで残すための保存形式（§24.30.2）。純粋関数。
/// <para>
/// 残すのは<b>見るためと、名前付きの応答を後のリクエストから引くため</b>（<c>{{login.response.body.$.token}}</c>）。
/// 送った側の <c>Authorization</c>／<c>Cookie</c> 等は表示用の写しなので伏せる（送り直すときは .http から組み直す）。
/// 応答の <c>Set-Cookie</c> も伏せる——クッキーは起動中の HttpClient が持つもので、再起動で消えるのが正しい。
/// 本文は <see cref="MaxBodyChars"/> で切り、バイナリは大きさだけ残す。
/// </para>
/// </summary>
public static class HttpResponseArchive
{
    public const int MaxBodyChars = 256 * 1024;
    public const int MaxEntries = 100;
    public const string Masked = "（保存時に伏せました）";

    private static readonly HashSet<string> SecretRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Authorization", "Proxy-Authorization", "Cookie", "X-Api-Key", "Api-Key" };
    private static readonly HashSet<string> SecretResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Set-Cookie" };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 日本語を \u にしない（開いて読めるように）
    };

    /// <summary>1 つの .http ぶん（鍵 → 最後の応答）を書く。新しい順に <see cref="MaxEntries"/> 件まで。</summary>
    public static string Serialize(string file, IEnumerable<KeyValuePair<string, HttpExchangeResult>> results)
    {
        var entries = results
            .OrderByDescending(p => p.Value.StartedAt)
            .Take(MaxEntries)
            .Select(p => ToEntry(p.Key, p.Value))
            .ToList();
        return JsonSerializer.Serialize(new ArchiveFile(1, file, entries), Options);
    }

    /// <summary>読めないもの（壊れた・版が違う）は空として扱う。保存は best-effort で、読めなくても送れなくはしない。</summary>
    public static IReadOnlyList<KeyValuePair<string, HttpExchangeResult>> Deserialize(string json)
    {
        try
        {
            var archive = JsonSerializer.Deserialize<ArchiveFile>(json, Options);
            if (archive is not { Version: 1, Entries: { } entries })
                return [];
            return entries.Where(e => e.Key is not null).Select(e => KeyValuePair.Create(e.Key, FromEntry(e))).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Entry ToEntry(string key, HttpExchangeResult r)
    {
        var body = r.IsBinary ? "" : r.BodyText;
        var truncated = r.IsTruncated || body.Length > MaxBodyChars;
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars];
        var display = r.IsBinary ? r.DisplayBody : (r.DisplayBody.Length > MaxBodyChars ? r.DisplayBody[..MaxBodyChars] : r.DisplayBody);
        return new Entry(
            key,
            r.Request.Method, r.Request.Url, Mask(r.Request.Headers, SecretRequestHeaders), r.Request.Body,
            r.Request.Unresolved.ToList(), r.Request.Error,
            r.StartedAt, r.Elapsed.TotalMilliseconds, r.StatusCode, r.ReasonPhrase, r.HttpVersion,
            Mask(r.Headers, SecretResponseHeaders), r.ContentType, r.BodyBytes, body, display, r.IsBinary, truncated, r.Error);
    }

    private static HttpExchangeResult FromEntry(Entry e) => new(
        new PreparedHttpRequest(e.Method ?? "GET", e.Url ?? "", Pairs(e.RequestHeaders), e.RequestBody,
            e.Unresolved ?? [], e.RequestError),
        e.StartedAt, TimeSpan.FromMilliseconds(e.ElapsedMs), e.StatusCode, e.ReasonPhrase, e.HttpVersion,
        Pairs(e.Headers), e.ContentType, e.BodyBytes, e.Body ?? "", e.DisplayBody ?? e.Body ?? "",
        e.IsBinary, e.IsTruncated, e.Error);

    private static List<string[]> Mask(IEnumerable<KeyValuePair<string, string>> headers, HashSet<string> secret)
        => headers.Select(h => new[] { h.Key, secret.Contains(h.Key) ? Masked : h.Value }).ToList();

    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(List<string[]>? headers)
        => headers?.Where(h => h.Length == 2).Select(h => KeyValuePair.Create(h[0], h[1])).ToList() ?? [];

    private sealed record ArchiveFile(int Version, string File, List<Entry> Entries);

    private sealed record Entry(
        string Key,
        string? Method, string? Url, List<string[]>? RequestHeaders, string? RequestBody,
        List<string>? Unresolved, string? RequestError,
        DateTimeOffset StartedAt, double ElapsedMs, int? StatusCode, string? ReasonPhrase, string? HttpVersion,
        List<string[]>? Headers, string? ContentType, long BodyBytes, string? Body, string? DisplayBody,
        bool IsBinary, bool IsTruncated, string? Error);
}
