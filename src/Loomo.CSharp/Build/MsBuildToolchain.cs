using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace sk0ya.Loomo.CSharp.Build;

/// <summary>MSBuild をどの実行ファイルで呼ぶか。<see cref="FileName"/> の後ろへ <see cref="PrefixArguments"/>、
/// その後ろへプロジェクトとスイッチを並べる（<c>dotnet msbuild …</c> と <c>MSBuild.exe …</c> を同じ形で扱う）。</summary>
public sealed record MsBuildInvocation(string FileName, IReadOnlyList<string> PrefixArguments, bool IsVisualStudio)
{
    /// <summary>.NET SDK 同梱の MSBuild（<c>dotnet msbuild</c>）。</summary>
    public static readonly MsBuildInvocation DotnetSdk = new("dotnet", ["msbuild"], false);

    /// <summary>起動情報へ実行ファイルと前置きの引数を入れる。プロジェクト以降の引数は呼び出し側が足す。</summary>
    public void ApplyTo(System.Diagnostics.ProcessStartInfo startInfo)
    {
        startInfo.FileName = FileName;
        foreach (var argument in PrefixArguments) startInfo.ArgumentList.Add(argument);
    }
}

/// <summary>
/// 旧形式（非SDK）のプロジェクトを見分け、それを扱える MSBuild を選ぶ。
///
/// <para><b>なぜ dotnet だけでは足りないのか</b>——<c>dotnet build</c>／<c>dotnet msbuild</c> は .NET SDK に
/// 同梱された MSBuild で動く。SDK 形式なら何の問題も無いが、<c>&lt;Project ToolsVersion=…&gt;</c> で始まる
/// 旧形式の .NET Framework プロジェクトは、その MSBuild では通らないものが多い：<c>packages.config</c> は
/// <c>dotnet restore</c> が復元しない、<c>COMReference</c> を解決できない、<c>$(VSToolsPath)</c> 経由で
/// Visual Studio にしか無い targets（旧 ASP.NET の WebApplication.targets 等）を import している。
/// これらは Visual Studio（または Build Tools）の <c>MSBuild.exe</c> なら通るので、旧形式にはそちらを使う。</para>
///
/// <para>MSBuild.exe が見つからない環境では従来どおり dotnet へ落とす——単純な旧形式プロジェクトなら
/// それでも通るので、「見つからないから何もしない」より悪くならない。</para>
/// </summary>
public static class MsBuildToolchain
{
    /// <summary>MSBuild.exe の場所を明示するための環境変数（探索より優先）。</summary>
    public const string MsBuildPathVariable = "LOOMO_MSBUILD";

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, bool IsLegacy)> LegacyCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<string?> VisualStudioMsBuild = new(LocateVisualStudioMsBuild);

    private static readonly Regex SolutionProjectLine = new(
        "^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+\\.(?:cs|vb|fs)proj)\"",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>プロジェクトが旧形式（SDK を使わない）か。読めないファイルは SDK 形式扱い（従来の経路のまま）。</summary>
    public static bool IsLegacyProject(string projectPath)
    {
        try
        {
            var full = Path.GetFullPath(projectPath);
            var stamp = File.GetLastWriteTimeUtc(full);
            if (LegacyCache.TryGetValue(full, out var cached) && cached.Stamp == stamp) return cached.IsLegacy;
            var legacy = ReadIsLegacy(full);
            LegacyCache[full] = (stamp, legacy);
            return legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or XmlException)
        {
            return false;
        }
    }

    /// <summary>ビルド対象（.csproj／.sln／.slnx）が旧形式のプロジェクトを含むか。
    /// ソリューションは中身のプロジェクトを1つずつ見る——SDK 形式と混在するソリューションでも、
    /// 1つでも旧形式があれば全体を MSBuild.exe で回す（SDK 形式は MSBuild.exe でも通る）。</summary>
    public static bool ContainsLegacyProject(string projectOrSolution)
    {
        var extension = Path.GetExtension(projectOrSolution);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            return SolutionProjects(projectOrSolution).Any(IsLegacyProject);
        return IsLegacyProject(projectOrSolution);
    }

    /// <summary>ビルド対象のプロジェクトが<b>すべて</b> .NET Framework 向けか（空なら false）。旧形式に加え、
    /// SDK 形式でも TFM が net4x／net3x だけのものを含む。.NET Framework だけのワークスペースに
    /// netcoredbg の導入を促さないための判定（それらは同梱の NetFx アダプタでデバッグする）。</summary>
    public static bool ContainsOnlyNetFrameworkProjects(string projectOrSolution)
    {
        var extension = Path.GetExtension(projectOrSolution);
        IReadOnlyList<string> projects = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                                         extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? SolutionProjects(projectOrSolution)
            : [projectOrSolution];
        return projects.Count > 0 && projects.All(p => IsLegacyProject(p) || TargetsOnlyNetFramework(p));
    }

    /// <summary>SDK 形式の <c>TargetFramework(s)</c> がすべて .NET Framework（net48 等）か。書かれていなければ false。</summary>
    internal static bool TargetsOnlyNetFramework(string projectPath)
    {
        try
        {
            var document = new XmlDocument();
            document.Load(projectPath);
            var frameworks = document.GetElementsByTagName("TargetFramework").Cast<XmlElement>()
                .Concat(document.GetElementsByTagName("TargetFrameworks").Cast<XmlElement>())
                .SelectMany(e => e.InnerText.Split(';'))
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            return frameworks.Count > 0 && frameworks.All(t =>
                Regex.IsMatch(t, "^net[1-4][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }

    /// <summary>対象に合う MSBuild を返す。旧形式を含み、かつ MSBuild.exe が見つかったときだけ
    /// Visual Studio 側を返し、それ以外は <see cref="MsBuildInvocation.DotnetSdk"/>。</summary>
    public static MsBuildInvocation For(string projectOrSolution)
        => ContainsLegacyProject(projectOrSolution) && FindVisualStudioMsBuild() is { } msbuild
            ? new MsBuildInvocation(msbuild, [], true)
            : MsBuildInvocation.DotnetSdk;

    /// <summary>Visual Studio／Build Tools の MSBuild.exe。見つからなければ null（結果はプロセス内でキャッシュ）。</summary>
    public static string? FindVisualStudioMsBuild()
    {
        var configured = Environment.GetEnvironmentVariable(MsBuildPathVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return VisualStudioMsBuild.Value;
    }

    /// <summary>Loomo が使う <c>-getProperty</c>／<c>-getItem</c> は MSBuild 17.8 から。それより古い MSBuild.exe
    /// （VS 2019・古い VS 2022）では評価も出力先の問い合わせも落ちるので、使わずに dotnet へ落とす。</summary>
    internal static bool SupportsPropertyQueries(string msbuildPath)
    {
        try
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(msbuildPath);
            return version.FileMajorPart > 17 || version.FileMajorPart == 17 && version.FileMinorPart >= 8;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return false; }
    }

    /// <summary>
    /// プロジェクト単体をビルドするときの <c>SolutionDir</c>。packages.config の復元先（<c>$(SolutionDir)packages</c>）と
    /// 旧形式 csproj の <c>..\packages\…</c> の HintPath はソリューションのフォルダーが前提で、Visual Studio は
    /// プロジェクトを単体でビルドするときもこれを渡している。渡さないと NuGet が「ソリューションが見つかりません」で落ちる。
    /// 上へ遡ってこのプロジェクトを載せた .sln を探し、無ければ .sln のある最初のフォルダー、それも無ければ
    /// プロジェクトの 1 つ上（旧形式の既定の配置）。
    /// </summary>
    public static string SolutionDirectoryFor(string projectPath)
    {
        var project = Path.GetFullPath(projectPath);
        string? firstWithSolution = null;
        for (var directory = Path.GetDirectoryName(project); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string[] solutions;
            try { solutions = Directory.GetFiles(directory, "*.sln"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (solutions.Length == 0) continue;
            firstWithSolution ??= directory;
            if (solutions.Any(sln => SolutionProjects(sln).Contains(project, StringComparer.OrdinalIgnoreCase)))
                return directory;
        }
        return firstWithSolution ?? Path.GetDirectoryName(Path.GetDirectoryName(project)) ?? Path.GetDirectoryName(project)!;
    }

    /// <summary>旧形式プロジェクトへ単体で MSBuild を掛けるときの <c>SolutionDir</c> の値（末尾 <c>/</c>）。
    /// ビルドだけでなく <c>TargetPath</c> などの問い合わせにも同じ値を渡すこと——<c>OutputPath</c> が
    /// <c>$(SolutionDir)bin\…</c> のプロジェクトで、ビルドと問い合わせの答えが食い違わないように。
    /// SDK 形式・ソリューションには null。</summary>
    public static string? SolutionDirPropertyFor(string projectOrSolution)
    {
        if (!Path.GetExtension(projectOrSolution).EndsWith("proj", StringComparison.OrdinalIgnoreCase) ||
            !IsLegacyProject(projectOrSolution)) return null;
        return SolutionDirectoryFor(projectOrSolution).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/";
    }

    /// <summary>ソリューションに載っているプロジェクトの絶対パス（存在するものだけ）。</summary>
    public static IReadOnlyList<string> SolutionProjects(string solutionPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
            var text = File.ReadAllText(solutionPath);
            IEnumerable<string> relative = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? SlnxProjects(text)
                : SolutionProjectLine.Matches(text).Cast<Match>().Select(m => m.Groups["path"].Value);
            return relative
                .Select(path => Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar))))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or XmlException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SlnxProjects(string text)
    {
        var document = new XmlDocument();
        document.LoadXml(text);
        foreach (XmlElement element in document.GetElementsByTagName("Project"))
        {
            var path = element.GetAttribute("Path");
            if (path.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    /// <summary>SDK 形式の印は3通り：ルートの <c>Sdk</c> 属性、直下の <c>&lt;Sdk Name=…/&gt;</c>、
    /// <c>Sdk</c> 属性付きの <c>&lt;Import&gt;</c>。どれも無く、代わりに素の <c>&lt;Import&gt;</c>
    /// （旧形式は必ず <c>Microsoft.CSharp.targets</c> 等を自分で import する）があれば旧形式。
    /// 中身の無い <c>&lt;Project /&gt;</c> のような判断材料の無いものは旧形式扱いしない（従来の経路のまま）。</summary>
    private static bool ReadIsLegacy(string projectPath)
    {
        using var reader = XmlReader.Create(projectPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Project") return false;
        if (!string.IsNullOrWhiteSpace(reader.GetAttribute("Sdk"))) return false;
        if (reader.IsEmptyElement) return false;
        var depth = reader.Depth;
        var importsTargets = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
            if (reader.LocalName == "Sdk") return false;
            if (reader.LocalName != "Import") continue;
            if (!string.IsNullOrWhiteSpace(reader.GetAttribute("Sdk"))) return false;
            importsTargets = true;
        }
        return importsTargets;
    }

    /// <summary>Visual Studio のインストーラーが書く <c>_Instances\*\state.json</c> から探す（vswhere と同じ情報源。
    /// 子プロセスを起こさずに済む）。新しい版を優先し、64bit の <c>Bin\amd64</c> があればそちらを使う。
    /// 見つからなければ vswhere に頼る。</summary>
    private static string? LocateVisualStudioMsBuild()
    {
        var candidates = new List<(Version Version, string Path)>();
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var instances = Path.Combine(programData, "Microsoft", "VisualStudio", "Packages", "_Instances");
            if (Directory.Exists(instances))
            {
                foreach (var state in Directory.EnumerateFiles(instances, "state.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(state));
                        var root = json.RootElement;
                        if (!root.TryGetProperty("installationPath", out var pathElement)) continue;
                        var installation = pathElement.GetString();
                        if (string.IsNullOrWhiteSpace(installation)) continue;
                        var version = root.TryGetProperty("installationVersion", out var v) &&
                                      Version.TryParse(v.GetString(), out var parsed) ? parsed : new Version(0, 0);
                        if (MsBuildUnder(installation) is { } msbuild && SupportsPropertyQueries(msbuild))
                            candidates.Add((version, msbuild));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (candidates.Count > 0)
            return candidates.OrderByDescending(c => c.Version).First().Path;
        return LocateWithVsWhere();
    }

    private static string? MsBuildUnder(string installationPath)
    {
        foreach (var relative in new[] { @"MSBuild\Current\Bin\amd64\MSBuild.exe", @"MSBuild\Current\Bin\MSBuild.exe" })
        {
            var path = Path.Combine(installationPath, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? LocateWithVsWhere()
    {
        try
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return null;
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(vswhere)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in new[] { "-latest", "-prerelease", "-products", "*", "-requires",
                         "Microsoft.Component.MSBuild", "-property", "installationPath" })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            // 1回きりで数十ミリ秒の子プロセス。プールではなく呼び出したスレッドで読み切る。
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var installation = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return installation is not null && MsBuildUnder(installation) is { } msbuild && SupportsPropertyQueries(msbuild)
                ? msbuild
                : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
