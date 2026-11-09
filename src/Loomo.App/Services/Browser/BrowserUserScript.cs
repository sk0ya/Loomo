namespace sk0ya.Loomo.App.Services;

/// <summary>設定から足したブラウザのスクリプトのコマンド（§24.106）の、走らせる前の整え。</summary>
internal static class BrowserUserScript
{
    private const string Scheme = "javascript:";

    /// <summary>ブックマークレットをそのまま貼れるように、<c>javascript:</c> を外す。外したときはブラウザと同じく
    /// URL として読み（<c>%20</c> 等を戻す）——ブックマークレットは符号化されて配られることが多い。
    /// 戻せない（<c>%</c> が壊れている）ならそのまま。走らせるものが無ければ null。</summary>
    public static string? Prepare(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return null;
        var body = script.Trim();
        if (body.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            body = body[Scheme.Length..];
            try { body = Uri.UnescapeDataString(body); }
            catch (UriFormatException) { }
        }
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }
}
