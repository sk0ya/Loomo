using System.Text;

namespace sk0ya.Loomo.Core.Http;

/// <summary>.http ファイルの1リクエスト（<c>###</c> で区切られた1区画）。文字列は<b>変数を解決する前</b>のまま持つ。</summary>
/// <param name="Index">ファイル内の何番目のリクエストか（0始まり）。名前の無いリクエストの呼び名にも使う。</param>
/// <param name="Name"><c># @name login</c> で付けた名前（応答を後のリクエストから <c>{{login.response…}}</c> で引く鍵）。無ければ null。</param>
/// <param name="Title"><c>### タイトル</c> の区切り行に書いた見出し。無ければ null。</param>
/// <param name="StartLine">区画の先頭行（0始まり）。キャレット位置からリクエストを決めるのに使う。</param>
/// <param name="EndLine">区画の最終行（0始まり・含む）。</param>
/// <param name="RequestLine">メソッドと URL が書かれた行（0始まり）。</param>
/// <param name="BodyFile"><c>&lt; ./body.json</c> で本文をファイルから読むときのパス（書かれたまま）。</param>
public sealed record HttpRequestBlock(
    int Index,
    string? Name,
    string? Title,
    int StartLine,
    int EndLine,
    int RequestLine,
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    string? BodyFile)
{
    /// <summary>一覧に出す呼び名（名前 → 見出し → 「#1」）。</summary>
    public string DisplayName => Name ?? Title ?? $"#{Index + 1}";

    /// <summary>この行がこのリクエストの区画に入っているか。</summary>
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>.http ファイル全体：ファイル変数（<c>@host = …</c>）とリクエストの並び。</summary>
public sealed record HttpRequestDocument(
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<HttpRequestBlock> Requests)
{
    /// <summary>キャレットの行が入っているリクエスト。区切りの間の空き行なら直前のもの、先頭より前なら最初のもの。</summary>
    public HttpRequestBlock? RequestAt(int line)
    {
        if (Requests.Count == 0)
            return null;
        var found = Requests.LastOrDefault(r => r.StartLine <= line);
        return found ?? Requests[0];
    }

    /// <summary>名前（<c># @name</c>）でリクエストを引く。</summary>
    public HttpRequestBlock? Find(string name)
        => Requests.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// .http ファイル（VS Code の REST Client／JetBrains の HTTP Client と同じ書式）を読む純ロジック。§24.30。
/// <para>
/// 読む書式：<c>###</c> 区切り（後ろの文字は見出し）／<c>#</c>・<c>//</c> のコメント／<c># @name 名前</c>／
/// ファイル変数 <c>@name = value</c>／リクエスト行 <c>METHOD URL [HTTP/1.1]</c>（メソッド省略は GET）／
/// 続く <c>?a=1</c>・<c>&amp;b=2</c> の行は URL へつなぐ／ヘッダー <c>Name: value</c>／空行の後が本文／
/// 本文が <c>&lt; ./file</c> ならファイルから読む。JetBrains の応答ハンドラ（<c>&gt; {% … %}</c>）と
/// 応答の書き出し（<c>&gt;&gt; file</c>）は本文に混ぜずに捨てる（実行はしない）。
/// </para>
/// <para>
/// 自前の書式を作らないのが方針——人がすでに書いている .http ファイルがそのまま動くこと、
/// そして Loomo の外（VS Code・Rider）でもそのまま動くことの方が、書式の拡張より価値がある。
/// </para>
/// </summary>
public static class HttpRequestFile
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT",
    };

    public static HttpRequestDocument Parse(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = new List<HttpRequestBlock>();

        var blockStart = 0;
        string? blockTitle = null;
        for (var i = 0; i <= lines.Length; i++)
        {
            var isSeparator = i < lines.Length && lines[i].TrimStart().StartsWith("###", StringComparison.Ordinal);
            if (i < lines.Length && !isSeparator)
                continue;
            // [blockStart, i) が1区画
            if (ParseBlock(lines, blockStart, i - 1, blockTitle, requests.Count, variables) is { } request)
                requests.Add(request);
            if (i < lines.Length)
            {
                var title = lines[i].TrimStart()[3..].Trim().TrimStart('#').Trim();
                blockTitle = title.Length > 0 ? title : null;
                blockStart = i;
            }
        }
        return new HttpRequestDocument(variables, requests);
    }

    private static HttpRequestBlock? ParseBlock(
        string[] lines, int start, int end, string? title, int index, Dictionary<string, string> variables)
    {
        string? name = null;
        var i = start;
        // 区切り行そのものは飛ばす
        if (i <= end && lines[i].TrimStart().StartsWith("###", StringComparison.Ordinal))
            i++;
        // リクエスト行の前：空行・コメント・@name・ファイル変数
        for (; i <= end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            if (TryReadComment(line, out var comment))
            {
                if (TryReadNameDirective(comment) is { } n)
                    name = n;
                continue;
            }
            if (line.StartsWith('@') && TryReadVariable(line, out var key, out var value))
            {
                variables[key] = value;
                continue;
            }
            break;
        }
        if (i > end)
            return null;   // 変数だけ・コメントだけの区画

        var requestLine = i;
        var (method, url) = ReadRequestLine(lines[i].Trim());
        i++;
        var urlBuilder = new StringBuilder(url);
        // URL の続き（?a=1 / &b=2 を行を分けて書ける）
        for (; i <= end; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('?') || line.StartsWith('&'))
                urlBuilder.Append(line);
            else
                break;
        }

        var headers = new List<KeyValuePair<string, string>>();
        for (; i <= end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                i++;
                break;
            }
            if (TryReadComment(line, out _))
                continue;
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        var bodyLines = new List<string>();
        var inHandler = false;
        for (; i <= end; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (inHandler)
            {
                if (line.EndsWith("%}", StringComparison.Ordinal))
                    inHandler = false;
                continue;
            }
            if (line.StartsWith("> {%", StringComparison.Ordinal))
            {
                inHandler = !line.EndsWith("%}", StringComparison.Ordinal);
                continue;
            }
            if (line.StartsWith(">>", StringComparison.Ordinal) || line.StartsWith("> ", StringComparison.Ordinal))
                continue;
            bodyLines.Add(raw.TrimEnd('\r'));
        }
        // 区画の末尾の空行・コメント（次の ### の上に書いた説明）は本文に入れない
        while (bodyLines.Count > 0 && (bodyLines[^1].Trim().Length == 0 || IsLineComment(bodyLines[^1])))
            bodyLines.RemoveAt(bodyLines.Count - 1);

        string? body = null;
        string? bodyFile = null;
        if (bodyLines.Count == 1 && bodyLines[0].TrimStart().StartsWith("< ", StringComparison.Ordinal))
            bodyFile = bodyLines[0].TrimStart()[2..].Trim();
        else if (bodyLines.Count > 0)
            body = string.Join("\r\n", bodyLines);

        return new HttpRequestBlock(index, name, title, start, end, requestLine,
            method, urlBuilder.ToString(), headers, body, bodyFile);
    }

    private static (string Method, string Url) ReadRequestLine(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ("GET", "");
        string method;
        int urlIndex;
        if (Methods.Contains(parts[0]))
        {
            method = parts[0].ToUpperInvariant();
            urlIndex = 1;
        }
        else
        {
            method = "GET";
            urlIndex = 0;
        }
        var urlParts = parts.Skip(urlIndex).ToList();
        // 末尾の HTTP/1.1 は版の指定（送り方は HttpClient に任せる）
        if (urlParts.Count > 1 && urlParts[^1].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            urlParts.RemoveAt(urlParts.Count - 1);
        return (method, string.Join(" ", urlParts));
    }

    private static bool TryReadComment(string line, out string comment)
    {
        if (line.StartsWith("//", StringComparison.Ordinal))
        {
            comment = line[2..].Trim();
            return true;
        }
        if (line.StartsWith('#'))
        {
            comment = line[1..].Trim();
            return true;
        }
        comment = "";
        return false;
    }

    private static bool IsLineComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith('#') || t.StartsWith("//", StringComparison.Ordinal);
    }

    private static string? TryReadNameDirective(string comment)
    {
        if (!comment.StartsWith("@name", StringComparison.Ordinal))
            return null;
        var rest = comment[5..].Trim().TrimStart('=').Trim();
        return rest.Length > 0 && !rest.Contains(' ') ? rest : null;
    }

    private static bool TryReadVariable(string line, out string key, out string value)
    {
        var eq = line.IndexOf('=');
        if (eq > 1)
        {
            key = line[1..eq].Trim();
            value = line[(eq + 1)..].Trim();
            if (key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
                return true;
        }
        key = value = "";
        return false;
    }
}
