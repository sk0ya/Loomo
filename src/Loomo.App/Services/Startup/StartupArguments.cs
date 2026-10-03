using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>Windows（エクスプローラー・既定のアプリ・リンク）から渡された「開いてほしいもの」。
/// フォルダーは部屋（ワークスペース）として、ファイルはエディタ／EditorSupport で、URL はブラウザペインで開く。</summary>
internal sealed record ExternalOpenRequest(
    string? WorkspaceFolder,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Urls)
{
    public static ExternalOpenRequest Empty { get; } = new(null, [], []);

    public bool IsEmpty => WorkspaceFolder is null && Files.Count == 0 && Urls.Count == 0;
}

/// <summary>起動引数から、ワークスペースフォルダー・ファイル・URL を取り出す。</summary>
internal static class StartupArguments
{
    private const string WorkspaceOption = "--workspace";

    /// <summary>ブラウザペインで開かせる指定。.html の関連付け（既定のブラウザとしての登録）が使う——
    /// 素の位置引数だと .html はエディタで開くファイルと区別できない。</summary>
    public const string BrowserOption = "--browser";

    /// <summary>
    /// <c>--workspace &lt;folder&gt;</c> と、互換性のための単独フォルダー引数を受け付ける。
    /// 存在しないパスやファイル引数は起動ワークスペースにしない。
    /// </summary>
    public static string? TryGetWorkspaceFolder(IReadOnlyList<string> args) => Parse(args).WorkspaceFolder;

    /// <summary>
    /// 起動引数を「開いてほしいもの」に分ける。
    /// <list type="bullet">
    /// <item><c>--workspace &lt;folder&gt;</c> または単独のフォルダー → ワークスペース（最初の1つだけ）。</item>
    /// <item>単独の既存ファイル → ファイル。</item>
    /// <item>単独の <c>http(s)://</c> URL、<c>--browser &lt;url|file&gt;</c> → URL（ファイルは file:/// にする）。</item>
    /// </list>
    /// 存在しないパスや解釈できない引数は黙って捨てる（関連付けから来る引数は利用者が打ったものではない）。
    /// </summary>
    public static ExternalOpenRequest Parse(IReadOnlyList<string> args)
    {
        string? folder = null;
        var files = new List<string>();
        var urls = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, WorkspaceOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && TryFullPath(args[++i]) is { } candidate && Directory.Exists(candidate))
                    folder ??= candidate;
                continue;
            }
            if (string.Equals(arg, BrowserOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && TryBrowserUrl(args[++i]) is { } url)
                    urls.Add(url);
                continue;
            }
            if (arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            if (TryWebUrl(arg) is { } webUrl)
            {
                urls.Add(webUrl);
                continue;
            }
            if (TryFullPath(arg) is not { } full)
                continue;
            if (Directory.Exists(full))
                folder ??= full;
            else if (File.Exists(full) && !files.Contains(full, StringComparer.OrdinalIgnoreCase))
                files.Add(full);
        }

        return new ExternalOpenRequest(folder, files, urls);
    }

    /// <summary>JumpTask の Arguments に入れるワークスペース引数。</summary>
    public static string FormatWorkspaceArgument(string folder)
        => $"{WorkspaceOption} {QuoteWindowsArgument(folder)}";

    private static string? TryWebUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;

    private static string? TryBrowserUrl(string value)
    {
        if (TryWebUrl(value) is { } url)
            return url;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
            return uri.AbsoluteUri;
        return TryFullPath(value) is { } full && File.Exists(full) ? new Uri(full).AbsoluteUri : null;
    }

    private static string? TryFullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return Path.GetFullPath(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    // Windows のコマンドラインでは、引用符の直前／末尾のバックスラッシュを
    // 2 倍しないと、ルートフォルダー (C:\) の末尾の \ が引用符をエスケープする。
    private static string QuoteWindowsArgument(string value)
    {
        var trailingSlashes = 0;
        for (var i = value.Length - 1; i >= 0 && value[i] == '\\'; i--)
            trailingSlashes++;

        return $"\"{value}{new string('\\', trailingSlashes)}\"";
    }
}
