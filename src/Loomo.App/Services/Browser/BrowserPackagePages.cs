using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace sk0ya.Loomo.App.Services;

public enum PackageEcosystem { Npm, NuGet }

/// <summary>パッケージのページ（npmjs.com・nuget.org）。<paramref name="Version"/> は URL に版があるときだけ。</summary>
public sealed record PackagePage(PackageEcosystem Ecosystem, string Name, string? Version)
{
    public string EcosystemLabel => Ecosystem == PackageEcosystem.Npm ? "npm" : "NuGet";
}

/// <summary>このワークスペースでそのパッケージを書いている場所1つ。<paramref name="Section"/> は npm の
/// dependencies／devDependencies 等、NuGet の PackageReference／PackageVersion（中央管理）。<paramref name="Line"/> は 1 始まり（分からなければ 0）。</summary>
public sealed record PackageUsage(string FilePath, string Version, string Section, int Line)
{
    /// <summary>この行の版を「dotnet add／npm install」で替えられるか。中央管理（Directory.Packages.props）や
    /// 版を書いていない参照は、コマンドではなくファイルを開いて直す。</summary>
    public bool CanSetVersion => Section is not "PackageVersion" && Version.Length > 0;
}

/// <summary>ワークスペースの中の、そのパッケージの使われ方：使っている場所と、まだ入れていないプロジェクト。</summary>
public sealed record PackageUsageScan(IReadOnlyList<PackageUsage> Usages, IReadOnlyList<string> OtherProjects, bool Truncated);

/// <summary>
/// パッケージのページ（ブラウザサポート §41）。URL からパッケージ名と版を読み、ワークスペースの <c>package.json</c>／
/// <c>*.csproj</c>（＋中央管理の <c>Directory.Packages.props</c>）からそのパッケージの使われ方を拾う。
/// 追加・版の変更は自分では書き換えず、<c>npm install</c>／<c>dotnet add package</c> を<b>見えるターミナル</b>で打つ（§30 の
/// 導入と同じ流儀。出力が人に見え、失敗もそこで分かる）。
/// </summary>
public static class BrowserPackagePages
{
    /// <summary>探すフォルダーの深さと、開くファイルの数の上限（大きなモノレポで固まらない）。</summary>
    private const int MaxDepth = 6;
    private const int MaxFiles = 400;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", ".idea", "dist", "out", "packages", "artifacts", "TestResults",
    };

    private static readonly string[] NpmSections = ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];

    public static PackagePage? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (host is "www.npmjs.com" or "npmjs.com" && segments.Count >= 2 && segments[0] == "package")
        {
            // /package/name・/package/@scope/name、版は /v/1.2.3
            var scoped = segments[1].StartsWith('@');
            if (scoped && segments.Count < 3)
                return null;
            var name = scoped ? $"{segments[1]}/{segments[2]}" : segments[1];
            var rest = segments.Skip(scoped ? 3 : 2).ToList();
            var version = rest.Count >= 2 && rest[0] == "v" ? rest[1] : null;
            return new PackagePage(PackageEcosystem.Npm, name, version);
        }
        if (host is "www.nuget.org" or "nuget.org" && segments.Count >= 2 && segments[0].Equals("packages", StringComparison.OrdinalIgnoreCase))
            return new PackagePage(PackageEcosystem.NuGet, segments[1], segments.Count >= 3 ? segments[2] : null);
        return null;
    }

    /// <summary>ワークスペースのフォルダーを歩いて使われ方を拾う（UI スレッドでは呼ばない）。</summary>
    public static PackageUsageScan Scan(IEnumerable<string> folders, PackagePage page)
    {
        var usages = new List<PackageUsage>();
        var others = new List<string>();
        var files = 0;
        var truncated = false;
        foreach (var folder in folders)
            foreach (var file in ProjectFiles(folder, page.Ecosystem))
            {
                if (++files > MaxFiles) { truncated = true; break; }
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var found = page.Ecosystem == PackageEcosystem.Npm ? ReadNpm(file, text, page.Name) : ReadNuGet(file, text, page.Name);
                if (found.Count > 0)
                    usages.AddRange(found);
                else if (IsProject(file, page.Ecosystem))
                    others.Add(file);
            }
        return new PackageUsageScan(usages, others, truncated);
    }

    /// <summary>入れる先として出すファイルか（Directory.*.props は入れる先ではない）。</summary>
    private static bool IsProject(string file, PackageEcosystem ecosystem)
        => ecosystem == PackageEcosystem.Npm || file.EndsWith("proj", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ProjectFiles(string root, PackageEcosystem ecosystem)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in entries.Where(f => Matches(Path.GetFileName(f), ecosystem)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return file;
            if (depth >= MaxDepth)
                continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children.OrderByDescending(c => c, StringComparer.OrdinalIgnoreCase))
                if (!SkippedFolders.Contains(Path.GetFileName(child)) && !Path.GetFileName(child).StartsWith('.'))
                    pending.Push((child, depth + 1));
        }
    }

    private static bool Matches(string name, PackageEcosystem ecosystem) => ecosystem == PackageEcosystem.Npm
        ? name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
        : name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
          || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
          || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
          || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase);

    /// <summary>package.json の依存の節から拾う（純粋）。行番号はキーの書かれた行。</summary>
    public static IReadOnlyList<PackageUsage> ReadNpm(string file, string text, string name)
    {
        var result = new List<PackageUsage>();
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var section in NpmSections)
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object
                    && deps.TryGetProperty(name, out var version))
                    result.Add(new PackageUsage(file, version.ValueKind == JsonValueKind.String ? version.GetString() ?? "" : "",
                        section, LineOf(text, $"\"{name}\"")));
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>MSBuild のファイルの PackageReference／PackageVersion（Include か Update が名前に一致）から拾う（純粋）。</summary>
    public static IReadOnlyList<PackageUsage> ReadNuGet(string file, string text, string id)
    {
        var result = new List<PackageUsage>();
        XDocument doc;
        try { doc = XDocument.Parse(text, LoadOptions.SetLineInfo); }
        catch (XmlException) { return result; }
        foreach (var element in doc.Descendants())
        {
            var kind = element.Name.LocalName;
            if (kind is not ("PackageReference" or "PackageVersion"))
                continue;
            var include = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "";
            if (!include.Equals(id, StringComparison.OrdinalIgnoreCase))
                continue;
            var version = (string?)element.Attribute("Version") ?? (string?)element.Attribute("VersionOverride")
                ?? element.Elements().FirstOrDefault(e => e.Name.LocalName is "Version" or "VersionOverride")?.Value ?? "";
            result.Add(new PackageUsage(file, version.Trim(), kind, ((IXmlLineInfo)element).LineNumber));
        }
        return result;
    }

    /// <summary>見えるターミナルに打つ1行（PowerShell）。<paramref name="target"/> は npm なら package.json、NuGet ならプロジェクト。
    /// <paramref name="section"/> が devDependencies なら <c>--save-dev</c> を保つ。</summary>
    public static string InstallCommand(PackagePage page, string target, string? version, string? section = null)
    {
        if (page.Ecosystem == PackageEcosystem.NuGet)
        {
            var add = $"dotnet add {Quote(target)} package {page.Name}";
            return string.IsNullOrWhiteSpace(version) ? add : $"{add} --version {version}";
        }
        var spec = string.IsNullOrWhiteSpace(version) ? page.Name : $"{page.Name}@{version}";
        var dev = section == "devDependencies" ? " --save-dev" : "";
        var dir = Path.GetDirectoryName(target) ?? ".";
        // スコープ付きの名前（@types/node）は PowerShell ではスプラットに読まれるので、引用して渡す。
        return $"Push-Location -LiteralPath {Quote(dir)}; npm install {Quote(spec)}{dev}; Pop-Location";
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    private static int LineOf(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? 0 : text.AsSpan(0, index).Count('\n') + 1;
    }

    /// <summary>名前が安全か（コマンドに入れる前の確かめ。npm／NuGet の名前に使える文字だけ）。</summary>
    public static bool IsSafeName(string name) => Regex.IsMatch(name, @"^@?[A-Za-z0-9][A-Za-z0-9._\-/]*$");

    /// <summary>コマンドに入れてよい版か。範囲指定（<c>^1.2</c>・<c>&gt;=2</c>）は PowerShell の演算子に化けるので入れない——
    /// ページの URL の版（いつも具体的な版）だけを渡す。</summary>
    public static bool IsSafeVersion(string? version) => version is null || Regex.IsMatch(version, @"^[A-Za-z0-9][A-Za-z0-9.\-+]{0,63}$");
}
