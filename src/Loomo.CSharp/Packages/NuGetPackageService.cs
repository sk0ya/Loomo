using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.CSharp.Packages;

/// <summary>プロジェクトに直接参照されている（top-level）パッケージ 1 件。TFM ごとに同じ版なら 1 件にまとめる。</summary>
public sealed record NuGetInstalledPackage(
    string ProjectPath, string Id, string RequestedVersion, string? ResolvedVersion, IReadOnlyList<string> Frameworks);

/// <summary><c>dotnet list package</c> の結果。<see cref="Problems"/> はプロジェクト単位の説明
/// （未 restore・packages.config 形式で対象外 等）。</summary>
public sealed record NuGetListResult(IReadOnlyList<NuGetInstalledPackage> Packages, IReadOnlyList<string> Problems);

/// <summary>フィードの検索結果 1 件。<see cref="Versions"/> は古い→新しい順。</summary>
public sealed record NuGetSearchResult(
    string Id, string LatestVersion, string? Description, string? Authors, long TotalDownloads,
    IReadOnlyList<string> Versions, string Source);

/// <summary><c>dotnet add|remove package</c> の結果。<see cref="Output"/> は人に見せる出力（標準出力＋標準エラー）。</summary>
public sealed record NuGetCommandResult(bool Succeeded, string Output);

/// <summary>
/// NuGet パッケージの一覧・検索・追加・更新・削除（Rider の NuGet ツールウィンドウ相当・設計書 §36）。
///
/// <para><b>ファイルを自分で書き換えない</b>。一覧は <c>dotnet list package --format json</c>、追加・更新・削除は
/// <c>dotnet add|remove package</c> に任せる——中央管理（Directory.Packages.props）の解決、restore、
/// TFM ごとの互換性チェックを SDK と同じ規則で行うため。検索とバージョン一覧だけは構成済みの v3 フィード
/// （<c>dotnet nuget list source</c>）へ HTTP で問い合わせる。認証付きフィードは対象外。</para>
/// </summary>
public sealed class NuGetPackageService
{
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<(string Source, string Type), string?> _serviceUrls = new();
    private readonly SemaphoreSlim _serviceGate = new(1, 1);

    // ── 一覧 ────────────────────────────────────────────────────────────────

    /// <summary><paramref name="target"/>（.sln／.slnx／.csproj）の直接参照パッケージ。</summary>
    public async Task<NuGetListResult> ListInstalledAsync(string target, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunDotnetAsync(
            ["list", target, "package", "--format", "json"], Path.GetDirectoryName(target), ct);
        var result = ParseListOutput(stdout);
        if (exit != 0 && result.Packages.Count == 0 && result.Problems.Count == 0)
            return new NuGetListResult([], [FirstMeaningfulLine(stderr, stdout) ?? $"dotnet list package が失敗しました（{exit}）"]);
        return result;
    }

    /// <summary><c>dotnet list package --format json</c> の出力を読む（純関数・テスト対象）。</summary>
    public static NuGetListResult ParseListOutput(string json)
    {
        var packages = new List<NuGetInstalledPackage>();
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return new NuGetListResult(packages, problems);

        var start = json.IndexOf('{');
        if (start < 0) return new NuGetListResult(packages, [json.Trim()]);
        using var doc = JsonDocument.Parse(json[start..]);
        var root = doc.RootElement;

        if (root.TryGetProperty("problems", out var rootProblems) && rootProblems.ValueKind == JsonValueKind.Array)
            foreach (var problem in rootProblems.EnumerateArray())
                if (Text(problem, "text") is { } text)
                    problems.Add(Text(problem, "project") is { } project ? $"{Path.GetFileName(project)}: {text}" : text);

        if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
            return new NuGetListResult(packages, problems);

        foreach (var project in projects.EnumerateArray())
        {
            var path = Text(project, "path") is { } p ? Path.GetFullPath(p) : "";
            if (!project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
                continue;

            var merged = new Dictionary<string, (string Requested, string? Resolved, List<string> Tfms)>(StringComparer.OrdinalIgnoreCase);
            foreach (var framework in frameworks.EnumerateArray())
            {
                var tfm = Text(framework, "framework") ?? "";
                if (!framework.TryGetProperty("topLevelPackages", out var top) || top.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var package in top.EnumerateArray())
                {
                    if (Text(package, "id") is not { } id) continue;
                    var requested = Text(package, "requestedVersion") ?? "";
                    var resolved = Text(package, "resolvedVersion");
                    if (merged.TryGetValue(id, out var existing))
                        existing.Tfms.Add(tfm);
                    else
                        merged[id] = (requested, resolved, [tfm]);
                }
            }
            packages.AddRange(merged.Select(pair => new NuGetInstalledPackage(
                path, pair.Key, pair.Value.Requested, pair.Value.Resolved, pair.Value.Tfms)));
        }
        return new NuGetListResult(packages, problems);
    }

    // ── 追加・更新・削除 ────────────────────────────────────────────────────

    /// <summary>追加、または既にあれば版の変更（<c>dotnet add package --version</c> は両方を兼ねる）。</summary>
    public async Task<NuGetCommandResult> InstallAsync(string projectPath, string id, string? version, CancellationToken ct = default)
    {
        var args = new List<string> { "add", projectPath, "package", id };
        if (!string.IsNullOrWhiteSpace(version)) { args.Add("--version"); args.Add(version!); }
        var (exit, stdout, stderr) = await RunDotnetAsync(args, Path.GetDirectoryName(projectPath), ct);
        return new NuGetCommandResult(exit == 0, JoinOutput(stdout, stderr));
    }

    public async Task<NuGetCommandResult> RemoveAsync(string projectPath, string id, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunDotnetAsync(
            ["remove", projectPath, "package", id], Path.GetDirectoryName(projectPath), ct);
        return new NuGetCommandResult(exit == 0, JoinOutput(stdout, stderr));
    }

    // ── 検索・版の一覧 ──────────────────────────────────────────────────────

    /// <summary>構成済みのフィード（無効なものと v3 でないものを除く）。何も取れなければ nuget.org。</summary>
    public async Task<IReadOnlyList<string>> GetSourcesAsync(string? workingDirectory, CancellationToken ct = default)
    {
        try
        {
            var (_, stdout, _) = await RunDotnetAsync(["nuget", "list", "source", "--format", "short"], workingDirectory, ct);
            var sources = ParseSources(stdout);
            return sources.Count > 0 ? sources : [NuGetOrg];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [NuGetOrg];
        }
    }

    /// <summary><c>dotnet nuget list source --format short</c> の出力（<c>E https://…</c>）を読む。</summary>
    public static IReadOnlyList<string> ParseSources(string output)
        => output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("E ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(url => url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                          url.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        IReadOnlyList<string> sources, string query, bool includePrerelease, CancellationToken ct = default)
    {
        var tasks = sources.Select(source => SearchSourceAsync(source, query, includePrerelease, ct)).ToList();
        var results = new List<NuGetSearchResult>();
        foreach (var task in tasks)
        {
            try { results.AddRange(await task); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        // 同じ ID が複数のフィードにあれば最初のフィード（構成順）を採る。
        return results.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> SearchSourceAsync(
        string source, string query, bool includePrerelease, CancellationToken ct)
    {
        var searchUrl = await ServiceUrlAsync(source, "SearchQueryService", ct);
        if (searchUrl is null) return [];
        var url = $"{searchUrl}?q={Uri.EscapeDataString(query)}&take=40&prerelease={(includePrerelease ? "true" : "false")}&semVerLevel=2.0.0";
        var json = await Http.GetStringAsync(url, ct);
        return ParseSearch(json, source);
    }

    /// <summary>v3 検索 API の応答を読む（純関数・テスト対象）。</summary>
    public static IReadOnlyList<NuGetSearchResult> ParseSearch(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        var results = new List<NuGetSearchResult>();
        foreach (var item in data.EnumerateArray())
        {
            if (Text(item, "id") is not { } id) continue;
            var versions = new List<string>();
            if (item.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                    if (Text(v, "version") is { } version) versions.Add(version);
            var authors = item.TryGetProperty("authors", out var a)
                ? a.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", a.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)))
                    : a.ValueKind == JsonValueKind.String ? a.GetString() : null
                : null;
            long downloads = item.TryGetProperty("totalDownloads", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt64() : 0;
            results.Add(new NuGetSearchResult(id, Text(item, "version") ?? versions.LastOrDefault() ?? "",
                Text(item, "description"), authors, downloads, versions, source));
        }
        return results;
    }

    /// <summary>パッケージの全版（古い→新しい）。検索に出てこない導入済みパッケージの更新先を知るため。</summary>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(
        IReadOnlyList<string> sources, string id, bool includePrerelease, CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            try
            {
                var baseUrl = await ServiceUrlAsync(source, "PackageBaseAddress/3.0.0", ct);
                if (baseUrl is null) continue;
                var json = await Http.GetStringAsync($"{baseUrl.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json", ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("versions", out var versions)) continue;
                var list = versions.EnumerateArray().Select(v => v.GetString() ?? "")
                    .Where(v => v.Length > 0 && (includePrerelease || !IsPrerelease(v))).ToList();
                if (list.Count > 0) return list;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        return [];
    }

    public static bool IsPrerelease(string version) => version.Contains('-');

    private async Task<string?> ServiceUrlAsync(string source, string type, CancellationToken ct)
    {
        await _serviceGate.WaitAsync(ct);
        try
        {
            if (_serviceUrls.TryGetValue((source, type), out var cached)) return cached;
            // 失敗（取り消し・一時的な通信エラー）は覚えない——覚えると、検索を打ち直して取り消した 1 回で
            // そのフィードが再起動まで「検索できない」ままになる。覚えるのは読めた結果だけ（無ければ null）。
            ct.ThrowIfCancellationRequested();
            string? url = null;
            try
            {
                var json = await Http.GetStringAsync(source, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("resources", out var resources))
                    foreach (var resource in resources.EnumerateArray())
                        if (Text(resource, "@type") is { } t && t.StartsWith(type, StringComparison.Ordinal))
                        {
                            url = Text(resource, "@id");
                            break;
                        }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }
            catch (JsonException)
            {
                // v3 の index.json ではない（v2 フィード等）。これは何度聞いても同じなので覚える。
            }
            _serviceUrls[(source, type)] = url;
            return url;
        }
        finally
        {
            _serviceGate.Release();
        }
    }

    // ── 版の比較・統合 ──────────────────────────────────────────────────────

    /// <summary>NuGet の版の大小（SemVer 2.0 の主要部分：数値の並び → プレリリースは低い）。</summary>
    public static int CompareVersions(string left, string right)
    {
        static (int[] Numbers, string Pre) Split(string v)
        {
            v = v.Trim().TrimStart('[', '(').Split(',')[0].Trim().TrimEnd(']', ')');
            var plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-');
            var core = dash >= 0 ? v[..dash] : v;
            var pre = dash >= 0 ? v[(dash + 1)..] : "";
            var numbers = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            return (numbers, pre);
        }
        var (a, aPre) = Split(left);
        var (b, bPre) = Split(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (aPre.Length == 0 && bPre.Length == 0) return 0;
        if (aPre.Length == 0) return 1;
        if (bPre.Length == 0) return -1;
        return string.Compare(aPre, bPre, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>同じパッケージがプロジェクト間で違う版で参照されているか（Rider の Consolidate の対象）。</summary>
    public static bool NeedsConsolidation(IEnumerable<NuGetInstalledPackage> installs)
        => installs.Select(i => i.ResolvedVersion ?? i.RequestedVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    // ── 子プロセス ──────────────────────────────────────────────────────────

    private static async Task<(int Exit, string Stdout, string Stderr)> RunDotnetAsync(
        IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo.FileName = "dotnet";
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        // 出力を読み取り付きで dotnet を起動すると、新しく立った MSBuild の常駐ノードが出力パイプを握って
        // EOF が来ず永久に待つ（restore を伴う add package で起きる）。常駐させない。
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";

        process.Start();
        // 出力はプールではなく専用スレッドで読む（ChildProcessIo・§31.16）。
        var stdoutTask = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "NuGet:stdout");
        var stderrTask = ChildProcessIo.ReadToEndAsync(process.StandardError, "NuGet:stderr");
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string JoinOutput(string stdout, string stderr)
        => string.Join(Environment.NewLine, new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));

    private static string? FirstMeaningfulLine(params string[] outputs)
        => outputs.SelectMany(o => o.Split('\n')).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
