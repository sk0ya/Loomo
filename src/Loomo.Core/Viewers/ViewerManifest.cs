using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Viewers;

/// <summary>Viewerが <c>viewer.json</c> に名前つきで書いたコマンド 1 つ（§44.2）。Viewerが回せるのはこれだけ——
/// 行は固定で、Viewerから渡る値は環境変数（<c>LOOMO_ARG_1</c>…）と変数 <c>$LoomoInput</c> にしか入らない。</summary>
/// <param name="Cwd"><c>workspace</c>（既定。ワークスペースの主フォルダー）か <c>viewer</c>（Viewerのフォルダー）。</param>
public sealed record ViewerCommand(string Name, string Command, string Cwd, int TimeoutSeconds)
{
    public const string CwdWorkspace = "workspace";
    public const string CwdViewer = "viewer";
    public const int DefaultTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 600;
}

/// <summary>Viewerが使える部屋の口の名前（<c>viewer.json</c> の <c>api</c>）。ここに無い名前は読み捨てる。
/// <c>state</c>・<c>toast</c> はViewerの中で閉じるので、挙げなくても使える。</summary>
public static class ViewerApi
{
    /// <summary>フォルダーの一覧・前に出ているファイルと行。</summary>
    public const string Workspace = "workspace";
    /// <summary>エディタ／ブラウザに出す。</summary>
    public const string Open = "open";
    /// <summary>ワークスペースの中のファイルを読む。</summary>
    public const string FsRead = "fs.read";
    /// <summary>リポジトリの一覧と状態。</summary>
    public const string Git = "git";
    /// <summary><c>commands</c> に書いたコマンドを裏で回す。</summary>
    public const string Run = "run";
    /// <summary>Viewerのページがネットワークへ出る（既定ではViewerのフォルダーの外を一切読めない）。</summary>
    public const string Net = "net";
    /// <summary>動き（§44.3）：録ったものを読む・いま録る・頼みごとの差分を開く。</summary>
    public const string Motion = "motion";
    /// <summary>言いつけ（§44.4）：言いつけを置く・進める・盆から採る／捨てる。</summary>
    public const string Orders = "orders";
    /// <summary>人間がパスを渡して開いたファイル（そのタブの 1 つだけ）を読む。ワークスペースの外でもよい——選んだのは人間。</summary>
    public const string Target = "target";

    public static readonly IReadOnlyList<string> All = [Workspace, Open, FsRead, Git, Run, Net, Motion, Orders, Target];

    /// <summary>許しを聞かずに使える口：読むか、人間の前に置くだけで、外へも出られないもの。ここに無い口（<c>run</c>＝任意の
    /// コマンド・<c>net</c>＝読んだ中身を外へ送れる・<c>motion</c>／<c>orders</c>＝裏で回す・作業ツリーへ当てる）を 1 つでも
    /// 挙げたViewerだけ人間に聞く。新しい口は足した時点では聞く側——安全と言えるときだけここへ足す。</summary>
    public static readonly IReadOnlyList<string> WithoutApproval = [Workspace, Open, FsRead, Git, Target];

    /// <summary>許しを聞くカードに出す、人間向けの 1 行。</summary>
    public static string Describe(string api) => api switch
    {
        Workspace => "ワークスペースのフォルダーと、前に出ているファイル・行を知る",
        Open => "ファイルをエディタに、URL をブラウザの新しいタブに出す",
        FsRead => "ワークスペースの中のファイルを読む",
        Git => "リポジトリの一覧と状態（ブランチ・変更のあるファイル）を読む",
        Run => "下に書いてあるコマンドを裏で回す（見えるターミナルには打たない）",
        Net => "ネットワークへ出る（外のページ・スクリプトを読む、外へ送る）",
        Motion => "録った動き（見どころの出力・テストの成否・絵）を読み、いまの作業ツリーで見どころを回して録る",
        Target => "開いたファイル（このタブに渡したパス）を読む",
        Orders => "言いつけを置く・外す・裏のワークツリーで Claude Code に進めさせる・仕上がりを作業ツリーへ当てる（採る）",
        _ => api,
    };
}

/// <summary>
/// Viewerの定義（<c>viewer.json</c>、§44.2）。<b>Viewerにできることの上限はここが決める</b>——使う口（<see cref="Api"/>）と
/// 回せるコマンド（<see cref="Commands"/>）。人間が許すのはこの 2 つの指紋（<see cref="Fingerprint"/>）で、
/// 題・説明・<c>index.html</c> の書き直しでは聞き直さない。I/O なし。
/// </summary>
public sealed record ViewerManifest(
    string Id, string Title, string Description, IReadOnlyList<string> Api, IReadOnlyList<ViewerCommand> Commands)
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex CommandNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>Viewerの名前（＝フォルダー名・オリジンの一部）。小文字の英数字とハイフンだけ——そのままホスト名に載る。</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    public bool Allows(string api) => Api.Contains(api);

    /// <summary>開く前に人間の許しが要るか。<see cref="ViewerApi.WithoutApproval"/> の口だけのViewerは聞かずに開く
    /// （書くのは Claude Code 自身の Write で、そちらの権限を通っている。Viewerの側で増える力が無い）。</summary>
    public bool NeedsApproval => Commands.Count > 0 || Api.Any(api => !ViewerApi.WithoutApproval.Contains(api));

    public ViewerCommand? Command(string name)
        => Commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal));

    /// <summary>許しの単位。口の顔ぶれとコマンドの行が同じなら同じ値（並び順・題・説明には左右されない）。</summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder();
            foreach (var api in Api.OrderBy(a => a, StringComparer.Ordinal))
                text.Append("api:").Append(api).Append('\n');
            foreach (var command in Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
                text.Append("cmd:").Append(command.Name).Append('\0').Append(command.Command).Append('\0')
                    .Append(command.Cwd).Append('\0').Append(command.TimeoutSeconds).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
        }
    }

    /// <summary><c>viewer.json</c> を読む。読めなければ null と、書いた人（Claude）がそのまま直せる英語の理由。</summary>
    public static ViewerManifest? Parse(string id, string json, out string? error)
    {
        error = null;
        if (!IsValidId(id))
        {
            error = $"Invalid viewer id \"{id}\": use lowercase letters, digits and hyphens (max 40 characters).";
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "viewer.json must be a JSON object.";
                return null;
            }

            var title = Text(root, "title") is { Length: > 0 } given ? given : id;
            var description = Text(root, "description") ?? "";

            var api = new List<string>();
            if (root.TryGetProperty("api", out var apiNode))
            {
                if (apiNode.ValueKind != JsonValueKind.Array)
                {
                    error = "\"api\" must be an array of strings.";
                    return null;
                }
                foreach (var item in apiNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name || !ViewerApi.All.Contains(name))
                    {
                        error = $"Unknown api \"{item}\". Known: {string.Join(", ", ViewerApi.All)}.";
                        return null;
                    }
                    if (!api.Contains(name))
                        api.Add(name);
                }
            }

            var commands = new List<ViewerCommand>();
            if (root.TryGetProperty("commands", out var commandsNode))
            {
                if (commandsNode.ValueKind != JsonValueKind.Object)
                {
                    error = "\"commands\" must be an object: { \"name\": { \"command\": \"...\" } }.";
                    return null;
                }
                foreach (var property in commandsNode.EnumerateObject())
                {
                    if (!CommandNamePattern.IsMatch(property.Name))
                    {
                        error = $"Invalid command name \"{property.Name}\".";
                        return null;
                    }
                    string? line;
                    var cwd = ViewerCommand.CwdWorkspace;
                    var timeout = ViewerCommand.DefaultTimeoutSeconds;
                    if (property.Value.ValueKind == JsonValueKind.String)
                        line = property.Value.GetString();
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        line = Text(property.Value, "command");
                        if (Text(property.Value, "cwd") is { } where)
                        {
                            if (where is not (ViewerCommand.CwdWorkspace or ViewerCommand.CwdViewer))
                            {
                                error = $"Command \"{property.Name}\": cwd must be \"workspace\" or \"viewer\".";
                                return null;
                            }
                            cwd = where;
                        }
                        if (property.Value.TryGetProperty("timeoutSeconds", out var seconds) && seconds.TryGetInt32(out var n))
                            timeout = Math.Clamp(n, 1, ViewerCommand.MaxTimeoutSeconds);
                    }
                    else
                        line = null;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        error = $"Command \"{property.Name}\" needs a non-empty \"command\" (a PowerShell command line).";
                        return null;
                    }
                    commands.Add(new ViewerCommand(property.Name, line!, cwd, timeout));
                }
            }
            if (commands.Count > 0 && !api.Contains(ViewerApi.Run))
            {
                error = "\"commands\" are declared but \"run\" is missing from \"api\".";
                return null;
            }
            return new ViewerManifest(id, title, description, api, commands);
        }
        catch (JsonException ex)
        {
            error = "viewer.json is not valid JSON: " + ex.Message;
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Viewerのページの URL（<c>https://&lt;id&gt;.viewer.loomo/…</c>）。Viewerごとにオリジンを分ける——<c>file.loomo</c>
/// （手元の HTML はどれも同じオリジン）やネットのページから、部屋の口に届かせない（§44.2）。</summary>
public static class ViewerUrl
{
    public const string HostSuffix = ".viewer.loomo";

    public static string Origin(string id) => $"https://{id}{HostSuffix}";

    public static string Index(string id) => Origin(id) + "/index.html";

    /// <summary>そのViewerのオリジンの URL か。</summary>
    public static bool IsOf(string id, string? uri)
        => TryParse(uri, out var owner, out _) && string.Equals(owner, id, StringComparison.Ordinal);

    /// <summary>URL をViewerの名前と、Viewerのフォルダーからの相対パス（<c>/</c> 区切り・空なら <c>index.html</c>）に分ける。</summary>
    public static bool TryParse(string? uri, out string id, out string relativePath)
    {
        id = "";
        relativePath = "";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var name = parsed.Host[..^HostSuffix.Length].ToLowerInvariant();
        if (!ViewerManifest.IsValidId(name))
            return false;
        id = name;
        var path = Uri.UnescapeDataString(parsed.AbsolutePath).TrimStart('/');
        relativePath = path.Length == 0 || path.EndsWith('/') ? path + "index.html" : path;
        return true;
    }
}

/// <summary>
/// 人間がパスを渡して開いた Viewer のタブ（§44.2「パスから開く」）。開いているタブの並びには、置き場の Viewer の id と並べて
/// <c>path:&lt;フルパス&gt;</c> の形で入る。そのままでは出せないファイルは、拡張子ごとに Claude Code が作った Viewer
/// （id は <see cref="IdFor"/>）で見せ、同じ拡張子の次のファイルからは使い回す。I/O なし。
/// </summary>
public static class ViewerPath
{
    public const string KeyPrefix = "path:";

    /// <summary>URL を渡して開いたタブの鍵の頭（<c>url:https://…</c>）。</summary>
    public const string UrlKeyPrefix = "url:";

    /// <summary>作った Viewer の id の頭。人が置いた Viewer と名前がぶつからないように分ける。</summary>
    public const string GeneratedPrefix = "file-";

    public static string KeyOf(string fullPath) => KeyPrefix + fullPath;

    public static string UrlKeyOf(string url) => UrlKeyPrefix + url;

    public static bool TryParseUrlKey(string? key, out string url)
    {
        url = "";
        if (key is null || !key.StartsWith(UrlKeyPrefix, StringComparison.Ordinal)
            || !Uri.TryCreate(key[UrlKeyPrefix.Length..], UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        url = key[UrlKeyPrefix.Length..];
        return true;
    }

    public static bool TryParseKey(string? key, out string fullPath)
    {
        fullPath = "";
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.Length == KeyPrefix.Length)
            return false;
        fullPath = key[KeyPrefix.Length..];
        return System.IO.Path.IsPathFullyQualified(fullPath);
    }

    /// <summary>そのファイルを見せる Viewer の id（拡張子ごと）。<c>data.sqlite</c> → <c>file-sqlite</c>、拡張子が無ければ
    /// <c>file-noext</c>、フォルダーは <c>file-folder</c>。</summary>
    public static string IdFor(string path, bool isDirectory)
    {
        if (isDirectory)
            return GeneratedPrefix + "folder";
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var clean = new string(ext.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
        if (clean.Length == 0)
            return GeneratedPrefix + "noext";
        return GeneratedPrefix + (clean.Length > 30 ? clean[..30] : clean);
    }

    public static bool IsGenerated(string id) => id.StartsWith(GeneratedPrefix, StringComparison.Ordinal);
}
