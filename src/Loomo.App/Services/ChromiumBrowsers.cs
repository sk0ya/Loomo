namespace sk0ya.Loomo.App.Services;

/// <summary>取り込み元にできるブラウザ1つ（＝プロファイル一式の置き場所と、動いているかの見分け方）。</summary>
/// <param name="DisplayName">画面に出す名前。</param>
/// <param name="UserDataFolder"><c>Local State</c> と各プロファイルフォルダーを含む親。</param>
/// <param name="ProcessNames">動作中かを見るプロセス名（拡張子なし）。</param>
public sealed record ChromiumBrowser(string DisplayName, string UserDataFolder, IReadOnlyList<string> ProcessNames)
{
    /// <summary>いま動いている。Cookie は<b>完全に終了していないと読めない</b>（実測：稼働中の
    /// <c>Network/Cookies</c> は共有違反で開けない。<c>Login Data</c> のほうは開ける）ので、
    /// 取り込みの前にこれを見て促す。</summary>
    public bool IsRunning => ProcessNames.Any(name =>
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch (InvalidOperationException) { return false; }
    });
}

/// <summary>取り込み元のプロファイル1つ（Chromium は「ユーザー」ごとにフォルダーが分かれる）。</summary>
public sealed record ChromiumProfileRef(ChromiumBrowser Browser, string FolderName, string DisplayName)
{
    /// <summary>プロファイルの実体（<c>Default</c> や <c>Profile 1</c>）。</summary>
    public string Path => System.IO.Path.Combine(Browser.UserDataFolder, FolderName);

    /// <summary>一覧に出す綴り。プロファイルが1つしか無くても、どのブラウザから来たかは常に出す。</summary>
    public string Label => $"{Browser.DisplayName} · {DisplayName}";
}

/// <summary>
/// 取り込み元になる Chromium 系ブラウザの検出（設計書 §21.5.4）。
///
/// <para><b>「入っているか」ではなく「プロファイルがあるか」で見る</b>——アンインストール済みでも
/// プロファイルは残るし、逆に入れた直後で一度も起動していないと <c>Local State</c> が無い。
/// 取り込めるかどうかを決めるのは実行ファイルではなくデータのほうなので、
/// レジストリではなく <c>User Data</c> の存在を見る。</para>
///
/// <para><b>Opera は入れていない</b>。あれだけプロファイルの形が違い（<c>User Data</c> を挟まず
/// <c>Opera Stable</c> 直下がプロファイル）、当てずっぽうで拾うと取り込み一覧に「あるのに読めない」
/// 項目が並ぶ。確かめられる形のものだけを並べる。</para>
/// </summary>
public static class ChromiumBrowsers
{
    /// <summary>プロファイルが実在する取り込み元を、よく使われる順に返す。</summary>
    public static IReadOnlyList<ChromiumBrowser> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            new ChromiumBrowser("Vivaldi", Path.Combine(local, "Vivaldi", "User Data"), new[] { "vivaldi" }),
            new ChromiumBrowser("Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data"), new[] { "chrome" }),
            new ChromiumBrowser("Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"), new[] { "msedge" }),
            new ChromiumBrowser("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), new[] { "brave" }),
        };
        return candidates.Where(b => File.Exists(Path.Combine(b.UserDataFolder, "Local State"))).ToList();
    }

    /// <summary>そのブラウザのプロファイル一覧。表示名は <c>Local State</c> の <c>profile.info_cache</c>
    /// が持っている（ユーザーが付けた名前）。読めなければフォルダー名で出す——
    /// <b>名前が分からないことを、プロファイルが無いことにしない</b>。</summary>
    public static IReadOnlyList<ChromiumProfileRef> ProfilesOf(ChromiumBrowser browser)
    {
        var names = ReadProfileNames(browser.UserDataFolder);
        var result = new List<ChromiumProfileRef>();
        foreach (var directory in EnumerateProfileFolders(browser.UserDataFolder))
        {
            var folder = Path.GetFileName(directory);
            names.TryGetValue(folder, out var display);
            result.Add(new ChromiumProfileRef(browser, folder,
                string.IsNullOrWhiteSpace(display) ? folder : display!));
        }
        return result;
    }

    /// <summary>プロファイルらしいフォルダー（<c>Default</c> と <c>Profile N</c>）。
    /// 目印は「ブックマークか履歴かログイン情報のどれかがある」こと——<c>System Profile</c> のような
    /// 中身の無いものを一覧に出さないため。</summary>
    private static IEnumerable<string> EnumerateProfileFolders(string userDataFolder)
    {
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(userDataFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var directory in directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            var isProfile = name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
            if (isProfile && ChromiumImportReader.HasAnyData(directory))
                yield return directory;
        }
    }

    private static Dictionary<string, string> ReadProfileNames(string userDataFolder)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(userDataFolder, "Local State")));
            if (!document.RootElement.TryGetProperty("profile", out var profile)
                || !profile.TryGetProperty("info_cache", out var cache)
                || cache.ValueKind != JsonValueKind.Object)
                return names;
            foreach (var entry in cache.EnumerateObject())
                if (entry.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    names[entry.Name] = name.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return names;
    }
}
