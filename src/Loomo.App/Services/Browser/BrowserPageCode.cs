namespace sk0ya.Loomo.App.Services;

/// <summary>ページのコードブロック1つ。<paramref name="Language"/> はページが付けた名前（無ければ空）。</summary>
public sealed record BrowserCodeBlock(string Language, string Text)
{
    public int LineCount => Text.Split('\n').Length;

    /// <summary>ターミナルへ送る形か：シェルの言語、または1行だけのもの。</summary>
    public bool LooksLikeCommand => BrowserPageCode.ShellLanguages.Contains(Language) || LineCount == 1;
}

/// <summary>
/// 表示中のページのコードブロック（ブラウザサポート §41：記事・ドキュメントのコードを、エディタ・コンポーザ・ターミナルへ）。
/// 抜き出しはページの中（JavaScript）で行い、結果は<b>データとして</b>受け取るだけ——ページから届く合図は受けない。
/// </summary>
public static class BrowserPageCode
{
    public const int MaxBlocks = 60;

    /// <summary><c>pre</c> を順に拾う。言語は <c>language-xx</c>／<c>lang-xx</c>／<c>highlight-source-xx</c> の class か
    /// <c>data-lang</c>。同じ中身は1つにする。結果は JSON の文字列（<c>ExecuteScriptAsync</c> がさらに JSON で包む）。</summary>
    public const string ExtractScript = """
(function(){
  var out=[], seen={};
  var pres=document.querySelectorAll('pre');
  for(var i=0;i<pres.length && out.length<60;i++){
    var pre=pres[i], code=pre.querySelector('code')||pre;
    var text=(code.innerText||code.textContent||'').replace(/\u00a0/g,' ').replace(/\s+$/,'');
    if(!text.trim() || text.length>20000 || seen[text]) continue;
    seen[text]=1;
    var cls=[code.className, pre.className, pre.parentElement ? pre.parentElement.className : ''].join(' ');
    if(typeof cls!=='string') cls='';
    var m=cls.match(/(?:language|lang|highlight-source|highlight)-([A-Za-z0-9_+#-]+)/);
    out.push({lang: m ? m[1] : (pre.getAttribute('data-lang')||code.getAttribute('data-lang')||''), text: text});
  }
  return JSON.stringify(out);
})()
""";

    public static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "shell", "console", "zsh", "powershell", "ps", "ps1", "pwsh", "cmd", "bat", "batch", "terminal", "shell-session",
    };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = ".cs", ["csharp"] = ".cs", ["c#"] = ".cs", ["fs"] = ".fs", ["fsharp"] = ".fs", ["vb"] = ".vb",
        ["ts"] = ".ts", ["typescript"] = ".ts", ["tsx"] = ".tsx", ["js"] = ".js", ["javascript"] = ".js", ["jsx"] = ".jsx",
        ["json"] = ".json", ["jsonc"] = ".json", ["yaml"] = ".yaml", ["yml"] = ".yaml", ["toml"] = ".toml", ["xml"] = ".xml",
        ["html"] = ".html", ["css"] = ".css", ["scss"] = ".scss", ["sql"] = ".sql", ["py"] = ".py", ["python"] = ".py",
        ["go"] = ".go", ["rust"] = ".rs", ["rs"] = ".rs", ["java"] = ".java", ["kotlin"] = ".kt", ["kt"] = ".kt",
        ["c"] = ".c", ["cpp"] = ".cpp", ["c++"] = ".cpp", ["md"] = ".md", ["markdown"] = ".md", ["dockerfile"] = ".dockerfile",
        ["powershell"] = ".ps1", ["ps1"] = ".ps1", ["pwsh"] = ".ps1", ["sh"] = ".sh", ["bash"] = ".sh", ["shell"] = ".sh",
    };

    /// <summary><c>ExecuteScriptAsync</c> の結果（JSON に包まれた JSON の文字列）を読む。読めなければ空。</summary>
    public static IReadOnlyList<BrowserCodeBlock> Parse(string? scriptResult)
    {
        try
        {
            if (string.IsNullOrEmpty(scriptResult) || JsonSerializer.Deserialize<string>(scriptResult) is not { } json)
                return [];
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => new BrowserCodeBlock(
                    e.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "",
                    e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""))
                .Where(b => b.Text.Trim().Length > 0)
                .Take(MaxBlocks)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return []; }
    }

    /// <summary>エディタの写しに付ける拡張子（言語が分からなければ .txt。無題タブは本文から言語を決める §24.28 が無いので）。</summary>
    public static string ExtensionFor(string language) => Extensions.TryGetValue(language, out var ext) ? ext : ".txt";

    /// <summary>ターミナルへ入れる形：1行のコマンドの頭のプロンプト（<c>$ </c>・<c>&gt; </c>・<c>PS&gt; </c>・<c>PS C:\x&gt; </c>）を外す。</summary>
    public static string AsCommand(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(StripPrompt).ToList();
        return string.Join('\n', lines).Trim();

        static string StripPrompt(string line)
        {
            var trimmed = line.TrimStart();
            // "# " は root のプロンプトでもあるが、シェルのコメントと見分けられないので外さない。
            if (trimmed.StartsWith("$ ", StringComparison.Ordinal) || trimmed.StartsWith("> ", StringComparison.Ordinal))
                return trimmed[2..];
            if (trimmed.StartsWith("PS", StringComparison.Ordinal) && trimmed.IndexOf("> ", StringComparison.Ordinal) is var gt and > 1 and < 80)
                return trimmed[(gt + 2)..];
            return line;
        }
    }

    /// <summary>行の頭の短い見出し（一覧の題）。</summary>
    public static string Preview(string text, int lines = 3)
    {
        var head = text.Replace("\r\n", "\n").Split('\n').Take(lines).Select(l => l.Length > 120 ? l[..120] + "…" : l);
        return string.Join('\n', head);
    }
}
