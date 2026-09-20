using System.Net.Http;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.App.Services;

/// <summary>拡張機能をどのストアから取るか。</summary>
public enum BrowserExtensionStoreKind
{
    Chrome,
    Edge,
}

/// <summary>導入済み拡張機能の出所（<c>browser-extensions.json</c> の1件）。
/// WebView2 は導入そのものをプロファイルに覚えているので、ここに持つのは
/// <b>後から辿り直せるようにするための出所</b>——どのフォルダーの実体を指しているか、
/// どこから取ってきたか（入れ直し・更新の手掛かり）。</summary>
public sealed class BrowserExtensionRecord
{
    public string Id { get; set; } = "";
    public string FolderPath { get; set; } = "";
    /// <summary>ストア由来ならその ID。フォルダー指定で入れたものは null。</summary>
    public string? StoreId { get; set; }
    public string? StoreKind { get; set; }
    public string? Name { get; set; }
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>manifest.json から読む、UI に要るぶんだけ。</summary>
public sealed record BrowserExtensionManifest(
    string? Name, string? Version, string? PopupPath, string? OptionsPath, string? IconPath);

/// <summary>
/// 拡張機能の実体（展開済みフォルダー）と出所を管理する。<b>WebView2 への登録は行わない</b>——
/// それには実体化済みの <c>CoreWebView2Profile</c> が要るのでシェル側（<c>ShellWindow.BrowserExtensions.cs</c>）の仕事で、
/// ここはファイルとネットワークだけを見る（＝テストできる範囲に閉じる）。
///
/// <para>ストアの URL/ID から入れる経路があるのは、<c>AddBrowserExtensionAsync</c> が
/// <b>展開済みフォルダーしか受け付けない</b>ため。素の WebView2 の作法どおりだと、使う側が毎回
/// crx を自分で剥がすことになり、1Password や Bitwarden を入れる現実的な道が無くなる。</para>
/// </summary>
public sealed class BrowserExtensionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>ストアの拡張機能 ID（a〜p の 32 文字）。</summary>
    private static readonly Regex IdPattern = new("^[a-p]{32}$", RegexOptions.Compiled);

    private readonly string _rootPath;
    private readonly string _recordPath;
    private readonly Func<HttpClient> _httpFactory;

    public BrowserExtensionStore() : this(DefaultRoot(), () => new HttpClient()) { }

    public BrowserExtensionStore(string rootPath, Func<HttpClient> httpFactory)
    {
        _rootPath = rootPath;
        _recordPath = Path.Combine(Path.GetDirectoryName(rootPath) ?? rootPath, "browser-extensions.json");
        _httpFactory = httpFactory;
    }

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "BrowserExtensions");

    /// <summary>ストアの URL か ID を受け取って ID を取り出す。
    /// 貼り付けるのは普通ストアのページの URL なので、そこから拾えないと使い物にならない。</summary>
    public static bool TryParseStoreId(string? input, out string id, out BrowserExtensionStoreKind kind)
    {
        id = "";
        kind = BrowserExtensionStoreKind.Chrome;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            return false;
        if (IdPattern.IsMatch(text))
        {
            id = text;
            return true;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;
        kind = uri.Host.Contains("microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase)
            ? BrowserExtensionStoreKind.Edge
            : BrowserExtensionStoreKind.Chrome;
        // どのストアも .../detail/<スラッグ>/<ID> の形。ID は末尾側にあるので後ろから探す。
        foreach (var segment in uri.Segments.Reverse().Select(s => s.Trim('/')))
            if (IdPattern.IsMatch(segment))
            {
                id = segment;
                return true;
            }
        return false;
    }

    /// <summary>ストアの<b>拡張機能ページを見ているか</b>。促しバーを出す判断に使う。
    /// <see cref="TryParseStoreId"/> は貼り付けを受けるので緩いが、こちらはホストを見て絞る——
    /// 32 文字の英字が入っているだけの無関係なページで「追加しますか」を出さないため。</summary>
    public static bool TryParseStoreDetail(string? url, out string id, out BrowserExtensionStoreKind kind)
    {
        id = "";
        kind = BrowserExtensionStoreKind.Chrome;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsStoreHost(uri.Host))
            return false;
        return TryParseStoreId(url, out id, out kind);
    }

    private static bool IsStoreHost(string host) =>
        host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>「ストアを開く」の行き先。</summary>
    public const string StoreHomeUrl = "https://chromewebstore.google.com/category/extensions";

    /// <summary>crx の配布 URL。どちらのストアも「更新チェック」のエンドポイントが実体を返す。</summary>
    public static string DownloadUrl(string id, BrowserExtensionStoreKind kind, string browserVersion)
        => kind == BrowserExtensionStoreKind.Edge
            ? "https://edge.microsoft.com/extensionwebstorebase/v1/crx"
                + $"?response=redirect&x=id%3D{id}%26installsource%3Dondemand%26uc"
            : "https://clients2.google.com/service/update2/crx"
                + $"?response=redirect&acceptformat=crx2,crx3&prodversion={browserVersion}"
                + $"&x=id%3D{id}%26installsource%3Dondemand%26uc";

    public string FolderFor(string id) => Path.Combine(_rootPath, id);

    /// <summary>ストアから crx を取り、<c>%APPDATA%/Loomo/BrowserExtensions/&lt;ID&gt;/</c> へ展開する。
    /// Chrome ストアで見つからなければ Edge アドオンも試す（貼られた ID だけでは出所が分からないため）。</summary>
    public async Task<CrxExtractResult> DownloadAsync(
        string id, BrowserExtensionStoreKind kind, string browserVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            return await DownloadFromAsync(id, kind, browserVersion, cancellationToken);
        }
        // Chrome ストアは<b>知らない ID に 204 No Content を返す</b>——エラーにはならないので、
        // 「crx ではない中身が返った」（<see cref="InvalidDataException"/>）も乗り換えの合図として扱う。
        // これが無いと、Edge 専用のアドオンを ID で貼ったときに「crx ファイルではありません」で終わる。
        catch (Exception ex) when (kind == BrowserExtensionStoreKind.Chrome
            && ex is HttpRequestException or InvalidDataException)
        {
            return await DownloadFromAsync(id, BrowserExtensionStoreKind.Edge, browserVersion, cancellationToken);
        }
    }

    private async Task<CrxExtractResult> DownloadFromAsync(
        string id, BrowserExtensionStoreKind kind, string browserVersion, CancellationToken cancellationToken)
    {
        using var http = _httpFactory();
        using var response = await http.GetAsync(
            DownloadUrl(id, kind, browserVersion), HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Directory.CreateDirectory(_rootPath);
        return CrxArchive.Extract(new MemoryStream(bytes, writable: false), FolderFor(id));
    }

    /// <summary>manifest.json から名前・版・ポップアップ・設定画面・アイコンを読む。
    /// ポップアップは MV3 が <c>action</c>、MV2 が <c>browser_action</c>——どちらも見る
    /// （新しいものだけ見ると、古い拡張機能のボタンが押しても何も出ないものになる）。
    ///
    /// <para>設定画面（MV3 は <c>options_ui.page</c>、古い形は <c>options_page</c>）も同じ理由で読む。
    /// WebView2 には <c>chrome://extensions</c> が無く、拡張機能のツールバーも描かれないので、
    /// <b>manifest から辿るほかに設定画面へ行く道が無い</b>——読まなければ、入れたのに設定できない
    /// 拡張機能（uBlock Origin のダッシュボードなど）になる。</para></summary>
    public static BrowserExtensionManifest? ReadManifest(string folderPath)
    {
        var path = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(path))
            return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is not JsonObject manifest)
                return null;
            var action = manifest["action"] as JsonObject ?? manifest["browser_action"] as JsonObject;
            var optionsUi = manifest["options_ui"] as JsonObject;
            return new BrowserExtensionManifest(
                Name: manifest["name"]?.GetValue<string>(),
                Version: manifest["version"]?.GetValue<string>(),
                PopupPath: action?["default_popup"]?.GetValue<string>(),
                OptionsPath: optionsUi?["page"]?.GetValue<string>() ?? manifest["options_page"]?.GetValue<string>(),
                IconPath: LargestIcon(action?["default_icon"]) ?? LargestIcon(manifest["icons"]));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>アイコンは <c>{"16":"a.png","48":"b.png"}</c> か単なる文字列で書かれる。
    /// 一覧に出すのは小さい表示なので、32px 以上で最も小さいもの（無ければ最大）を選ぶ。</summary>
    private static string? LargestIcon(JsonNode? icons)
    {
        switch (icons)
        {
            case JsonValue value when value.TryGetValue<string>(out var single):
                return single;
            case JsonObject map:
                var sizes = map
                    .Select(pair => (Size: int.TryParse(pair.Key, out var n) ? n : 0, Path: pair.Value?.GetValue<string>()))
                    .Where(pair => pair.Path is { Length: > 0 })
                    .OrderBy(pair => pair.Size)
                    .ToList();
                return sizes.FirstOrDefault(s => s.Size >= 32).Path ?? sizes.LastOrDefault().Path;
            default:
                return null;
        }
    }

    // ── 出所の記録 ─────────────────────────────────────────────────────
    /// <summary>記録を読む。<b>「無い」と「読めない」を区別する</b>（読めないときは null）——
    /// 掃除（<see cref="CleanOrphanFolders"/>）はこの一覧を「残すもの」の正本にするので、
    /// 壊れたファイルを空と読むと展開済みの拡張機能を<b>全部消す</b>ことになる。</summary>
    private List<BrowserExtensionRecord>? TryLoadRecords()
    {
        if (!File.Exists(_recordPath))
            return new List<BrowserExtensionRecord>();
        try
        {
            return JsonSerializer.Deserialize<List<BrowserExtensionRecord>>(
                File.ReadAllText(_recordPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>表示や突き合わせ用（読めなければ空。拡張機能自体は WebView2 側に残っているので、
    /// 出所が分からなくなるだけ）。</summary>
    public List<BrowserExtensionRecord> LoadRecords() => TryLoadRecords() ?? new List<BrowserExtensionRecord>();

    /// <summary>記録を書く。<b>置き換えは一時ファイル経由</b>——書き込みの途中で落ちると
    /// 切れた JSON が残り、次に読んだときに「記録が無い」と見えてしまう（上の注記のとおり危険）。
    /// 書けたかを返す（書けなくても導入自体は成立している）。</summary>
    public bool SaveRecords(IEnumerable<BrowserExtensionRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
            var temporary = _recordPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records.ToList(), JsonOptions));
            File.Move(temporary, _recordPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>出所を1件覚える。<b>読めない記録の上に書かない</b>——<see cref="LoadRecords"/> は
    /// 「読めない」を空として返すので、それを土台にすると<b>この1件だけの記録に置き換わる</b>。
    /// そうなると他の拡張機能のフォルダーは「記録に無い＝取り残し」に見え、次に一覧を開いた掃除
    /// （<see cref="CleanOrphanFolders"/>）がまとめて消す——あちらが「読めないときは何もしない」と
    /// 決めているのに、ここで readable な嘘の全体像を作ってしまうと、その用心ごと無効になる。
    /// 覚えられたかを返す。</summary>
    public bool Remember(BrowserExtensionRecord record)
    {
        if (TryLoadRecords() is not { } records)
            return false;
        records.RemoveAll(r => string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));
        records.Add(record);
        return SaveRecords(records);
    }

    /// <summary>記録を消し、<b>自分たちが展開したフォルダーだけ</b>を消す。
    /// 使う側が指定したフォルダーはその人の持ち物なので触らない。
    /// 記録が読めないときは<b>何もしない</b>（理由は <see cref="Remember"/> と同じ）。</summary>
    /// <param name="cleanFolders">実体フォルダーの掃除まで行うか。<b>導入の最中は false</b>——
    /// 記録が書かれるのは登録が済んだ後なので、展開中のフォルダーが取り残しに見える。</param>
    public bool Forget(string id, bool cleanFolders = true)
    {
        if (TryLoadRecords() is not { } records)
            return false;
        records.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        var saved = SaveRecords(records);
        if (cleanFolders)
            CleanOrphanFolders();
        return saved;
    }

    /// <summary>記録に残っていない展開フォルダーを消す。
    ///
    /// <para><b>削除した直後は消せないことがある</b>——WebView2 がまだそのフォルダーを掴んでいる
    /// （実機で確認済み）。ここで失敗しても記録上は消えているので、<b>次に一覧を開いたとき</b>や
    /// アプリを起動し直したあとに同じ掃除が走って回収される。放っておくと展開済みの拡張機能
    /// （数十MB になる）がいつまでも残る。</para></summary>
    public void CleanOrphanFolders()
    {
        if (!Directory.Exists(_rootPath))
            return;
        // 記録が読めないときは何もしない（空と読んで全部消すよりは、残るほうがまし）。
        if (TryLoadRecords() is not { } records)
            return;
        var known = records
            .Where(r => IsInsideRoot(r.FolderPath))
            .Select(r => Path.GetFullPath(r.FolderPath).TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.GetDirectories(_rootPath))
        {
            if (known.Contains(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)))
                continue;
            // 展開中の置き場（<see cref="CrxArchive"/> が作る `<ID>.tmp`）は取り残しではない。
            if (folder.EndsWith(CrxArchive.StagingSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private bool IsInsideRoot(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var root = Path.GetFullPath(_rootPath).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFullPath(path)
            .StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
