namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ローカルファイルを、<b>コピーもフォルダーの割り当ても無しに</b> WebView2 で開くための URL。
/// パスそのものを URL に載せる——<c>C:\proj\docs\a b.html</c> → <c>https://file.loomo/C:/proj/docs/a%20b.html</c>、
/// UNC の <c>\\srv\share\x.html</c> → <c>https://file.loomo/unc/srv/share/x.html</c>。中身は
/// <see cref="LocalFileServer"/> が WebView2 の <c>WebResourceRequested</c> でディスクから直接返す。
///
/// <para><c>file://</c> で開くとオリジンが <c>null</c> になり、<c>fetch()</c>・ES モジュール・Worker が
/// 軒並み弾かれる。それを避ける定番は <c>SetVirtualHostNameToFolderMapping</c> だが、あれは
/// <b>フォルダーを先に割り当てる</b>仕組みで、どこを割り当てたかという状態を view ごとに持つことになる
/// （割り当ての外へ出た相対パスは解決しない・WebView2 を作り直すと割り当てが消える）。ここではパスが URL に
/// 入っているので状態が要らず、URL だけで同じファイルに辿り着く——復元も履歴もそのまま効く。
/// 相対パスはふつうの URL 解決でディスクの並びどおりに解ける（<c>../img/x.png</c> も）。
/// どこまで読ませるかは別の話で、<see cref="LocalFileAccess"/> が決める。</para>
/// </summary>
internal static class LocalFileUrl
{
    public const string Host = "file.loomo";
    public const string Origin = "https://" + Host;
    private const string UncSegment = "unc";

    /// <summary><c>file://</c> の代わりにこの URL で開くファイル（相対の fetch やモジュールが要る文書）。</summary>
    private static readonly string[] PageExtensions = [".html", ".htm", ".xhtml"];

    public static bool IsPage(string path)
        => PageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsLocalFileUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>絶対パス → URL。</summary>
    public static string FromPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return Origin + "/" + UncSegment + "/" + EscapeSegments(full[2..]);
        // ドライブ（"C:"）だけはエスケープしない——"C%3A" より読める。
        var drive = full[..2];
        return Origin + "/" + drive + "/" + EscapeSegments(full[3..]);
    }

    /// <summary><c>file://</c> のページ（<see cref="IsPage"/>）なら、同じファイルを指すこちらの URL。
    /// クエリとフラグメントは引き継ぐ。それ以外は null（PDF・画像・メディアは <c>file://</c> のままで困らない）。</summary>
    public static string? TryFromFileUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return null;
        string path;
        try { path = parsed.LocalPath; }
        catch (InvalidOperationException) { return null; }
        if (!IsPage(path) || !Path.IsPathFullyQualified(path))
            return null;
        return FromPath(path) + parsed.Query + parsed.Fragment;
    }

    /// <summary>URL → 絶対パス。こちらの URL でない・パスとして組めないなら false。
    /// 末尾が <c>/</c> の URL はフォルダー（区切りで終わるパス）になる。</summary>
    public static bool TryToPath(string? url, out string path)
    {
        path = "";
        if (!IsLocalFileUrl(url))
            return false;
        var uri = new Uri(url!);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2)
            return false;

        string root;
        string[] rest;
        if (IsDrive(segments[0]))
        {
            root = char.ToUpperInvariant(segments[0][0]) + @":\";
            rest = segments[1..];
        }
        else if (string.Equals(segments[0], UncSegment, StringComparison.OrdinalIgnoreCase)
                 && segments.Length >= 4 && segments[1].Length > 0 && segments[2].Length > 0)
        {
            root = $@"\\{segments[1]}\{segments[2]}\";
            rest = segments[3..];
        }
        else
        {
            return false;
        }

        // エスケープした区切り（%5C）やドライブ・代替データストリーム（":"）を名前の中へ持ち込ませない。
        if (rest.Any(s => s.IndexOfAny(['\\', ':']) >= 0))
            return false;
        try
        {
            path = Path.GetFullPath(root + string.Join('\\', rest));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDrive(string segment)
        => segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':';

    private static string EscapeSegments(string relative)
        => string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
}
