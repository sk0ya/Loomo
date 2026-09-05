using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace sk0ya.Loomo.CSharp.Debug;

/// <summary>IIS Expressのapplicationhost.configから解決したsite構成。</summary>
internal sealed record IisExpressHostConfiguration(string ConfigPath, string SiteName);

/// <summary>IIS Expressを起動するための、シェルに依存しない仕様。
/// <see cref="IisExpressLaunchCommand.Build"/>は通常実行用のPowerShellへ変換し、
/// DAP起動時はこの値を<see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>へ渡す。</summary>
public sealed record IisExpressLaunchSpec(
    string ExecutablePath,
    string ProjectDirectory,
    string PortSwitch,
    int Port,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? ApplicationHostConfigPath = null,
    string? SiteName = null)
{
    public bool UsesApplicationHostConfiguration
        => !string.IsNullOrWhiteSpace(ApplicationHostConfigPath)
            && !string.IsNullOrWhiteSpace(SiteName);

    public IReadOnlyList<string> ProcessArguments =>
        UsesApplicationHostConfiguration
            ? [
                "/config:" + ApplicationHostConfigPath,
                "/site:" + SiteName,
                "/systray:false",
            ]
            : [
                "/path:" + ProjectDirectory,
                "/" + PortSwitch + ":" + Port,
                "/systray:false",
            ];
}

/// <summary>launchSettings.jsonのIIS Expressプロファイルを、可視ターミナルから実行する
/// PowerShellコマンドへ変換する。通常実行では<see cref="Build"/>を使い、DAP attachでは
/// <see cref="CreateSpec"/>の引数をプロセスへ直接渡す。通常実行側の値はすべてPowerShellの
/// 単引用符でエスケープしてコマンド注入を防ぐ。</summary>
public static class IisExpressLaunchCommand
{
    /// <summary>IIS Expressの通常実行／DAP attachで共有する起動仕様を作る。</summary>
    public static IisExpressLaunchSpec? CreateSpec(
        string projectPath,
        LaunchSettingsProfile profile,
        out string? error,
        string? executablePath = null,
        string? applicationHostConfigPath = null)
    {
        error = null;
        if (!profile.IsIisExpress)
        {
            error = "IIS Expressプロファイルではありません。";
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            error = "IIS Expressのプロジェクトフォルダーが見つかりません。";
            return null;
        }

        var executable = executablePath ?? FindExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = "iisexpress.exeが見つかりません。IIS Expressをインストールしてください。";
            return null;
        }

        if (!TryGetPort(profile, out var switchName, out var port))
        {
            error = "IIS ExpressのapplicationUrlまたはsslPortから待受ポートを解決できません。";
            return null;
        }
        if (profile.CommandLineArgs is { Length: > 0 })
        {
            error = "IIS ExpressプロファイルのcommandLineArgsは実行形式に変換できません。";
            return null;
        }

        var host = IisExpressHostConfigurationResolver.Find(
            projectDirectory, switchName, port, applicationHostConfigPath);
        return new IisExpressLaunchSpec(executable, projectDirectory, switchName, port,
            new Dictionary<string, string>(profile.EnvironmentVariables,
                StringComparer.OrdinalIgnoreCase), host?.ConfigPath, host?.SiteName);
    }

    public static string? Build(
        string projectPath,
        LaunchSettingsProfile profile,
        out string? error,
        string? executablePath = null,
        string? applicationHostConfigPath = null)
    {
        var spec = CreateSpec(projectPath, profile, out error, executablePath, applicationHostConfigPath);
        if (spec is null) return null;

        var environment = BuildEnvironmentPrefix(profile.EnvironmentVariables, out error);
        if (error is not null) return null;
        var arguments = spec.ProcessArguments.Select(QuoteProcessArgument);
        return environment + "& " + Quote(spec.ExecutablePath) + " " + string.Join(" ", arguments);
    }

    private static bool TryGetPort(
        LaunchSettingsProfile profile, out string switchName, out int port)
    {
        switchName = "port";
        port = 0;
        var urls = profile.ApplicationUrl?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseHttpUrl)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList() ?? [];
        var preferred = urls.FirstOrDefault(url => url.Scheme == Uri.UriSchemeHttp);
        if (preferred is not null && preferred.Port > 0)
        {
            port = preferred.Port;
            return true;
        }
        var secure = urls.FirstOrDefault(url => url.Scheme == Uri.UriSchemeHttps);
        if (secure is not null && secure.Port > 0)
        {
            switchName = "sslport";
            port = secure.Port;
            return true;
        }
        if (profile.IisExpressSslPort is int sslPort && sslPort > 0)
        {
            switchName = "sslport";
            port = sslPort;
            return true;
        }
        return false;
    }

    private static Uri? ParseHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    private static string BuildEnvironmentPrefix(
        IReadOnlyDictionary<string, string> variables, out string? error)
    {
        error = null;
        if (variables.Count == 0) return "";
        var parts = new List<string>();
        foreach (var pair in variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Length == 0 || !IsEnvironmentName(pair.Key))
            {
                error = $"環境変数名が不正です: {pair.Key}";
                return "";
            }
            parts.Add("$env:" + pair.Key + " = " + Quote(pair.Value) + "; ");
        }
        return string.Concat(parts);
    }

    private static bool IsEnvironmentName(string value)
        => (char.IsLetter(value[0]) || value[0] == '_')
            && value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "IIS Express", "iisexpress.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "IIS Express", "iisexpress.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Quote(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string QuoteProcessArgument(string argument)
    {
        foreach (var prefix in new[] { "/path:", "/config:", "/site:" })
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix + Quote(argument[prefix.Length..]);
        }
        return argument;
    }
}

/// <summary>Visual Studioが生成するapplicationhost.configから、プロジェクトに対応するsiteを解決する。
/// 構成ファイルは読み取り専用で扱い、XMLが壊れている場合や対象siteが見つからない場合は
/// 呼び出し側が従来の/path方式へフォールバックできるようnullを返す。</summary>
internal static class IisExpressHostConfigurationResolver
{
    public static IisExpressHostConfiguration? Find(
        string projectDirectory,
        string portSwitch,
        int port,
        string? explicitConfigPath)
    {
        var candidates = string.IsNullOrWhiteSpace(explicitConfigPath)
            ? FindCandidatePaths(projectDirectory)
            : [Path.GetFullPath(explicitConfigPath)];

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var match = TryFindSite(candidate, projectDirectory, portSwitch, port);
            if (match is not null) return match;
        }
        return null;
    }

    private static IEnumerable<string> FindCandidatePaths(string projectDirectory)
    {
        var directory = new DirectoryInfo(projectDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, ".vs", "config", "applicationhost.config");
            directory = directory.Parent;
        }
    }

    private static IisExpressHostConfiguration? TryFindSite(
        string configPath,
        string projectDirectory,
        string portSwitch,
        int port)
    {
        try
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = System.Xml.XmlReader.Create(configPath, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var expectedProtocol = string.Equals(portSwitch, "sslport", StringComparison.OrdinalIgnoreCase)
                ? "https"
                : "http";

            foreach (var site in document.Descendants().Where(element => element.Name.LocalName == "site"))
            {
                var name = (string?)site.Attribute("name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var physicalPath = site.Descendants()
                    .Where(element => element.Name.LocalName == "virtualDirectory")
                    .Where(element => string.Equals((string?)element.Attribute("path"), "/", StringComparison.Ordinal))
                    .Select(element => (string?)element.Attribute("physicalPath"))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!PathsEqual(physicalPath, projectDirectory)) continue;

                var hasMatchingBinding = site.Descendants()
                    .Where(element => element.Name.LocalName == "binding")
                    .Any(binding => BindingMatches(binding, expectedProtocol, port));
                if (!hasMatchingBinding) continue;
                return new IisExpressHostConfiguration(Path.GetFullPath(configPath), name);
            }
        }
        catch (System.Xml.XmlException)
        {
            // Visual Studioの生成途中などで壊れた構成は、/path方式へフォールバックする。
        }
        catch (IOException)
        {
            // 読み取り競合も同様にフォールバックする。
        }
        catch (UnauthorizedAccessException)
        {
            // 権限のない構成は起動対象にしない。
        }
        return null;
    }

    private static bool BindingMatches(XElement binding, string protocol, int port)
    {
        if (!string.Equals((string?)binding.Attribute("protocol"), protocol, StringComparison.OrdinalIgnoreCase))
            return false;
        var bindingInformation = (string?)binding.Attribute("bindingInformation");
        if (string.IsNullOrWhiteSpace(bindingInformation)) return false;
        var parts = bindingInformation.Split(':');
        return parts.Length >= 3
            && int.TryParse(parts[1], out var bindingPort)
            && bindingPort == port;
    }

    private static bool PathsEqual(string? configuredPath, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(configuredPath.Trim()),
                Path.GetFullPath(projectDirectory),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
