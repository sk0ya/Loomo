using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Http;

/// <summary>名前付きリクエスト（<c># @name</c>）の最後の応答。後のリクエストから
/// <c>{{login.response.body.$.token}}</c> / <c>{{login.response.headers.Location}}</c> で引く。</summary>
public sealed record HttpNamedResponse(IReadOnlyList<KeyValuePair<string, string>> Headers, string Body);

/// <summary>
/// <c>{{…}}</c> の解決（純ロジック・テスト対象）。§24.30。REST Client と同じ名前の変数を同じ意味で読む：
/// <list type="bullet">
/// <item>ファイル変数 <c>{{host}}</c>（値の中の <c>{{…}}</c> も解決する）</item>
/// <item><c>{{$guid}}</c>・<c>{{$timestamp}}</c>・<c>{{$randomInt 1 100}}</c>・<c>{{$datetime iso8601}}</c>・<c>{{$localDatetime rfc1123}}</c></item>
/// <item><c>{{$processEnv NAME}}</c>（環境変数）・<c>{{$dotenv NAME}}</c>（.http と同じフォルダーの .env）</item>
/// <item><c>{{name.response.body.$.a.b[0]}}</c>・<c>{{name.response.headers.X}}</c>（名前付きリクエストの最後の応答）</item>
/// </list>
/// 解決できないものは<b>そのまま残し</b>、名前を <see cref="Unresolved"/> に集める——黙って空文字にすると、
/// 送ってから「なぜ 404 か」を探すことになる。
/// </summary>
public sealed class HttpVariableResolver
{
    private static readonly Regex Placeholder = new(@"\{\{\s*(.+?)\s*\}\}", RegexOptions.Compiled);
    private const int MaxDepth = 10;

    private readonly IReadOnlyDictionary<string, string> _fileVariables;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, string?> _dotenv;
    private readonly Func<string, HttpNamedResponse?> _responses;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);

    public HttpVariableResolver(
        IReadOnlyDictionary<string, string> fileVariables,
        Func<string, string?>? environment = null,
        Func<string, string?>? dotenv = null,
        Func<string, HttpNamedResponse?>? responses = null,
        Func<DateTimeOffset>? now = null)
    {
        _fileVariables = fileVariables;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _dotenv = dotenv ?? (_ => null);
        _responses = responses ?? (_ => null);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>解決できなかった変数（<c>{{</c> と <c>}}</c> の中身）。</summary>
    public IReadOnlyCollection<string> Unresolved => _unresolved;

    public string Resolve(string? text) => Resolve(text ?? "", 0);

    private string Resolve(string text, int depth)
    {
        if (depth > MaxDepth || !text.Contains("{{", StringComparison.Ordinal))
            return text;
        return Placeholder.Replace(text, match =>
        {
            var expression = match.Groups[1].Value;
            if (Evaluate(expression, depth) is { } value)
                return value;
            _unresolved.Add(expression);
            return match.Value;
        });
    }

    private string? Evaluate(string expression, int depth)
    {
        if (expression.StartsWith('$'))
            return EvaluateSystem(expression);
        if (_fileVariables.TryGetValue(expression, out var value))
            return Resolve(value, depth + 1);
        if (expression.Contains(".response.", StringComparison.Ordinal))
            return EvaluateResponse(expression);
        return null;
    }

    private string? EvaluateSystem(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var now = _now();
        switch (parts[0])
        {
            case "$guid":
                return Guid.NewGuid().ToString();
            case "$timestamp":
                return now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "$randomInt":
            {
                var min = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 0;
                var max = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 1000;
                return (max > min ? Random.Shared.Next(min, max) : min).ToString(CultureInfo.InvariantCulture);
            }
            case "$datetime":
                return FormatDate(now.ToUniversalTime(), parts.Length > 1 ? parts[1] : "iso8601");
            case "$localDatetime":
                return FormatDate(now, parts.Length > 1 ? parts[1] : "iso8601");
            case "$processEnv":
                return parts.Length > 1 ? _environment(parts[1]) : null;
            case "$dotenv":
                return parts.Length > 1 ? _dotenv(parts[1]) : null;
            default:
                return null;
        }
    }

    private static string FormatDate(DateTimeOffset value, string format) => format switch
    {
        "rfc1123" => value.ToString("r", CultureInfo.InvariantCulture),
        "iso8601" => value.ToString("o", CultureInfo.InvariantCulture),
        _ => value.ToString(format.Trim('"', '\''), CultureInfo.InvariantCulture),
    };

    /// <summary><c>name.response.body.$.a.b</c> / <c>name.response.body.*</c> / <c>name.response.headers.X</c>。</summary>
    private string? EvaluateResponse(string expression)
    {
        var marker = expression.IndexOf(".response.", StringComparison.Ordinal);
        var name = expression[..marker];
        var rest = expression[(marker + ".response.".Length)..];
        if (_responses(name) is not { } response)
            return null;
        if (rest.StartsWith("headers.", StringComparison.Ordinal))
        {
            var header = rest["headers.".Length..];
            return response.Headers.FirstOrDefault(h => string.Equals(h.Key, header, StringComparison.OrdinalIgnoreCase)).Value;
        }
        if (rest == "body" || rest == "body.*")
            return response.Body;
        if (rest.StartsWith("body.$", StringComparison.Ordinal))
            return JsonPath.Select(response.Body, rest["body.".Length..]);
        return null;
    }
}

/// <summary>JSONPath の小さな部分集合（<c>$.a.b[0].c</c> / <c>$['a']</c>）。応答から値を1つ取り出すのに足りる分だけ。</summary>
public static class JsonPath
{
    /// <summary>値を文字列で返す（文字列はそのまま、それ以外は JSON 表記）。見つからなければ null。</summary>
    public static string? Select(string json, string path)
    {
        if (!path.StartsWith('$'))
            return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
            var current = document.RootElement;
            foreach (var step in Steps(path[1..]))
            {
                if (step is int index)
                {
                    if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                        return null;
                    current = current[index];
                }
                else if (step is string key)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                        return null;
                    current = next;
                }
                else
                {
                    return null;
                }
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
        }
    }

    private static IEnumerable<object?> Steps(string path)
    {
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var start = ++i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                    i++;
                if (i > start)
                    yield return path[start..i];
            }
            else if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                {
                    yield return null;
                    yield break;
                }
                var inner = path[(i + 1)..close].Trim();
                i = close + 1;
                if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    yield return index;
                else
                    yield return inner.Trim('\'', '"');
            }
            else
            {
                yield return null;
                yield break;
            }
        }
    }
}

/// <summary>.env の読み取り（<c>KEY=value</c>・<c>#</c> コメント・両端の引用符を外す）。</summary>
public static class DotEnvFile
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            result[line[..eq].Trim()] = value;
        }
        return result;
    }
}
