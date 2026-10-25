using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Safety;

/// <summary>
/// トークン・鍵・パスワードらしい文字列を見つけて伏せる（純ロジック・テスト対象）。§24.66。
/// <para>workspaces.json のように<b>再起動のあとも残る</b>場所へターミナルの文字を書く前に通す。atuin の secrets_filter と
/// 同じ考え方で、<b>形がはっきりしたものだけ</b>を拾う——取りこぼしより誤検知の方が害が大きい（普通のコマンドが
/// 最近のコマンドから消える・出力が穴だらけになる）。だから <c>-p</c> のような短い引数や、長いだけの英数字は拾わない。</para>
/// <para>名前の付いた形（<c>$env:GITHUB_TOKEN=…</c>・<c>--password …</c>・<c>Authorization: Bearer …</c>・URL の
/// <c>user:pass@</c>）は<b>値だけ</b>を伏せ、名前は残す。値が変数の参照（<c>$…</c>）なら秘密そのものではないので伏せない。</para>
/// </summary>
public static class SecretRedactor
{
    /// <summary>伏せた所に置く文字。</summary>
    public const string Mask = "[伏せた秘密]";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>それ自体が秘密の形（丸ごと伏せる）。</summary>
    private static readonly Regex[] WholeTokens =
    [
        // PEM の秘密鍵（BEGIN〜END を丸ごと。END が無ければ BEGIN の行から末尾まで）
        new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)", Options),
        new(@"\bgh[pousr]_[A-Za-z0-9]{30,}", Options),                       // GitHub
        new(@"\bgithub_pat_[A-Za-z0-9_]{22,}", Options),                     // GitHub（細かい権限）
        new(@"\bglpat-[A-Za-z0-9_\-]{20,}", Options),                        // GitLab
        new(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}", Options),             // OpenAI / Anthropic
        new(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", Options),                      // AWS のアクセスキー
        new(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", Options),                    // Slack
        new(@"\bhv[sb]\.[A-Za-z0-9_\-]{20,}", Options),                      // HashiCorp Vault
        new(@"\bAIza[0-9A-Za-z_\-]{35}", Options),                           // Google API キー
        new(@"\bnpm_[A-Za-z0-9]{36}\b", Options),                            // npm
        new(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", Options), // JWT
    ];

    /// <summary>名前の後ろの値だけを伏せる形。グループ <c>name</c> は残し、<c>value</c> を伏せる。</summary>
    private static readonly Regex[] NamedValues =
    [
        // Authorization: Bearer xxx / Basic xxx / token xxx
        new(@"(?<name>\bAuthorization\s*:\s*(?:Bearer|Basic|token)\s+)(?<value>[^\s""']+)", Options | RegexOptions.IgnoreCase),
        // --password xxx / --token=xxx / -Password xxx / --api-key xxx（短い -p は拾わない）
        new(@"(?<name>(?<![\w-])--?(?:password|passwd|pwd|token|secret|api-?key|client-?secret|access-?token)(?:[=:]|\s+))(?<value>""[^""]*""|'[^']*'|[^\s""'$][^\s]*)",
            Options | RegexOptions.IgnoreCase),
        // $env:FOO_TOKEN = "xxx"（PowerShell）
        new(@"(?<name>\$env:\w*(?:TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|_PAT|ACCESS_KEY)\w*\s*=\s*)(?<value>""[^""]*""|'[^']*'|[^\s""'$][^\s;]*)",
            Options | RegexOptions.IgnoreCase),
        // FOO_TOKEN=xxx / export FOO_SECRET=xxx / set FOO_PASSWORD=xxx（sh・cmd）
        new(@"(?<name>(?<![\w$:])\w*(?:TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|ACCESS_KEY)\w*=)(?<value>""[^""]*""|'[^']*'|[^\s""'$%][^\s;]*)",
            Options | RegexOptions.IgnoreCase),
        // https://user:pass@host
        new(@"(?<name>\b[a-z][a-z0-9+.\-]*://[^\s:/@]+:)(?<value>[^\s@/]+)(?=@)", Options | RegexOptions.IgnoreCase),
    ];

    /// <summary>秘密らしい文字列を含むか。</summary>
    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var regex in WholeTokens)
            if (regex.IsMatch(text))
                return true;
        foreach (var regex in NamedValues)
            if (regex.IsMatch(text))
                return true;
        return false;
    }

    /// <summary>秘密らしい文字列を <see cref="Mask"/> に置き換える。<paramref name="count"/> は伏せた箇所の数。</summary>
    public static string Redact(string? text, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var replaced = 0;
        var result = text;
        foreach (var regex in WholeTokens)
            result = regex.Replace(result, _ => { replaced++; return Mask; });
        foreach (var regex in NamedValues)
            result = regex.Replace(result, m =>
            {
                if (m.Groups["value"].Value == Mask)
                    return m.Value;   // 丸ごと伏せた後のもの
                replaced++;
                return m.Groups["name"].Value + Mask;
            });
        count = replaced;
        return result;
    }

    /// <summary><see cref="Redact(string?, out int)"/> の数の要らない版。</summary>
    public static string Redact(string? text) => Redact(text, out _);
}
