using System.Text.Json;

namespace sk0ya.Loomo.CSharp.Debug;

/// <summary>.NETのlaunchSettings.jsonに定義された起動プロファイル。</summary>
public sealed record LaunchSettingsProfile(
    string Name,
    string CommandName,
    string? ExecutablePath,
    string? CommandLineArgs,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    bool LaunchBrowser = false,
    string? ApplicationUrl = null,
    string? LaunchUrl = null,
    int? IisExpressSslPort = null)
{
    public string DisplayName => IsDebugSupported
        ? Name
        : $"{Name}（未対応: {CommandName}）";

    /// <summary>IIS Expressのプロファイルか。</summary>
    public bool IsIisExpress
        => string.Equals(CommandName, "IISExpress", StringComparison.OrdinalIgnoreCase);

    /// <summary>Solution Explorerの通常実行で扱える形式か。IIS Expressは専用ランチャーで
    /// 起動し、通常実行とDAP attachの両方で利用する。</summary>
    public bool IsRunSupported => IsSupported || IsIisExpress;

    /// <summary>Debugボタンで扱える形式か。IIS Expressは専用プロセスを起動して
    /// netcoredbgがattachするため、通常のdotnet launchとは別経路だがDebug対応である。</summary>
    public bool IsDebugSupported => IsSupported || IsIisExpress;

    /// <summary>ASP.NETのapplicationUrlとlaunchUrlから、ブラウザで開く実効URLを作る。
    /// 複数の待受URLは最初のHTTP(S)を選び、未対応スキームや不正値は実行しない。</summary>
    public string? BrowserUrl
    {
        get
        {
            if (!LaunchBrowser || string.IsNullOrWhiteSpace(ApplicationUrl)) return null;
            var baseUrl = ApplicationUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(IsHttpUrl);
            if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return null;

            var launch = LaunchUrl?.Trim();
            if (string.IsNullOrEmpty(launch)) return baseUri.AbsoluteUri;
            if (Uri.TryCreate(launch, UriKind.Absolute, out var absolute))
                return IsHttpUrl(absolute.AbsoluteUri) ? absolute.AbsoluteUri : null;
            launch = launch.TrimStart('~', '/');
            return Uri.TryCreate(baseUri, launch, out var combined) && IsHttpUrl(combined.AbsoluteUri)
                ? combined.AbsoluteUri : null;
        }
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>LoomoのDAP起動へ安全に変換できる形式だけを実行候補とする。</summary>
    public bool IsSupported
        => string.Equals(CommandName, "Project", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CommandName, "Executable", StringComparison.OrdinalIgnoreCase);
}

/// <summary>プロジェクトのProperties/launchSettings.jsonを読み取るC#専用パーサー。</summary>
public static class LaunchSettingsProfileParser
{
    public static IReadOnlyList<LaunchSettingsProfile> ParseProject(string projectPath, out string? error)
    {
        error = null;
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory)) return Array.Empty<LaunchSettingsProfile>();
        var path = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(path)) return Array.Empty<LaunchSettingsProfile>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("profiles", out var profiles)
                || profiles.ValueKind != JsonValueKind.Object)
                return Array.Empty<LaunchSettingsProfile>();

            var iisExpress = document.RootElement.TryGetProperty("iisSettings", out var iisSettings)
                && iisSettings.ValueKind == JsonValueKind.Object
                && iisSettings.TryGetProperty("iisExpress", out var iisValue)
                && iisValue.ValueKind == JsonValueKind.Object
                ? iisValue
                : (JsonElement?)null;
            var iisApplicationUrl = iisExpress is { } iis
                ? StringValue(iis, "applicationUrl")
                : null;
            var iisSslPort = iisExpress is { } iisPort
                ? IntValue(iisPort, "sslPort")
                : null;

            var result = new List<LaunchSettingsProfile>();
            foreach (var property in profiles.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var value = property.Value;
                var commandName = StringValue(value, "commandName") ?? "";
                var isIisExpress = string.Equals(commandName, "IISExpress", StringComparison.OrdinalIgnoreCase);
                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (value.TryGetProperty("environmentVariables", out var variables)
                    && variables.ValueKind == JsonValueKind.Object)
                {
                    foreach (var variable in variables.EnumerateObject())
                    {
                        if (variable.Value.ValueKind == JsonValueKind.String)
                            environment[variable.Name] = variable.Value.GetString() ?? "";
                    }
                }

                result.Add(new LaunchSettingsProfile(
                    property.Name,
                    commandName,
                    StringValue(value, "executablePath"),
                    StringValue(value, "commandLineArgs"),
                    StringValue(value, "workingDirectory"),
                    environment,
                    BoolValue(value, "launchBrowser"),
                    StringValue(value, "applicationUrl") ?? (isIisExpress ? iisApplicationUrl : null),
                    StringValue(value, "launchUrl"),
                    isIisExpress ? iisSslPort : null));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return Array.Empty<LaunchSettingsProfile>();
        }
    }

    private static string? StringValue(JsonElement value, string property)
        => value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool BoolValue(JsonElement value, string property)
        => value.TryGetProperty(property, out var element)
            && element.ValueKind is JsonValueKind.True;

    private static int? IntValue(JsonElement value, string property)
        => value.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var number)
            ? number
            : null;

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
