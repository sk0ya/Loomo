using System;

namespace sk0ya.Loomo.Services;

/// <summary>
/// Azure DevOps の組織（オンプレの Azure DevOps Server ならコレクション）。REST の土台 URL を持つ。
/// </summary>
public sealed record AzureDevOpsOrganization(string Name, string BaseUrl)
{
    /// <summary>dev.azure.com 形式の組織。</summary>
    public static AzureDevOpsOrganization FromName(string name)
        => new(name, $"https://dev.azure.com/{name}");

    /// <summary>git のリモート URL から組織を読む。対応する形：
    /// <list type="bullet">
    /// <item><c>https://[user@]dev.azure.com/{org}/{project}/_git/{repo}</c></item>
    /// <item><c>https://{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}</c></item>
    /// <item><c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c></item>
    /// <item><c>{org}@vs-ssh.visualstudio.com:v3/{org}/{project}/{repo}</c></item>
    /// <item><c>https://{server}/tfs/{collection}/{project}/_git/{repo}</c>（Azure DevOps Server）</item>
    /// </list></summary>
    public static bool TryParseRemote(string? remoteUrl, out AzureDevOpsOrganization organization)
    {
        organization = null!;
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return false;
        var value = remoteUrl.Trim();

        // SSH（scp 形式）：ホストの後ろの「v3/{org}/...」から組織を取る。
        var sshPath = SshPathAfter(value, "ssh.dev.azure.com:") ?? SshPathAfter(value, "vs-ssh.visualstudio.com:");
        if (sshPath is not null)
        {
            var parts = sshPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("v3", StringComparison.OrdinalIgnoreCase) && IsName(parts[1]))
            {
                organization = value.Contains("vs-ssh.visualstudio.com", StringComparison.OrdinalIgnoreCase)
                    ? FromLegacyHost(parts[1])
                    : FromName(parts[1]);
                return true;
            }
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == "ssh"))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            // ssh://git@ssh.dev.azure.com/v3/{org}/... も同じ並び。
            var index = segments.Length > 0 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (segments.Length <= index || !IsName(Uri.UnescapeDataString(segments[index])))
                return false;
            organization = FromName(Uri.UnescapeDataString(segments[index]));
            return true;
        }
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var name = uri.Host[..^".visualstudio.com".Length];
            if (name.Equals("vs-ssh", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length < 2 || !IsName(segments[1]))
                    return false;
                organization = FromLegacyHost(segments[1]);
                return true;
            }
            if (!IsName(name))
                return false;
            organization = FromLegacyHost(name);
            return true;
        }

        // Azure DevOps Server：「…/{collection}/{project}/_git/{repo}」。_git の2つ手前までがコレクション。
        var git = Array.FindIndex(segments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (git >= 2 && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            var collection = string.Join('/', segments[..(git - 1)]);
            organization = new AzureDevOpsOrganization(
                Uri.UnescapeDataString(segments[git - 2]),
                $"{uri.Scheme}://{uri.Authority}/{collection}");
            return true;
        }
        return false;
    }

    /// <summary>人が設定に書いた値から組織を読む。リモート URL・組織（コレクション）URL・組織名だけ、のどれでもよい。
    /// URL はそのまま土台にする（TaskAzure の「組織 URL」と同じ値を貼れる）。</summary>
    public static bool TryParseUserInput(string? text, out AzureDevOpsOrganization organization)
    {
        organization = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var value = text.Trim().TrimEnd('/');
        if (value.Contains("/_git/", StringComparison.OrdinalIgnoreCase) && TryParseRemote(value, out organization))
            return true;
        if (!value.Contains("://", StringComparison.Ordinal) && value.Contains('.', StringComparison.Ordinal))
            value = "https://" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var name = segments.Length > 0
                ? Uri.UnescapeDataString(segments[^1])
                : uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host[..^".visualstudio.com".Length]
                    : uri.Host;
            organization = new AzureDevOpsOrganization(name, $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}");
            return true;
        }
        if (IsName(value))
        {
            organization = FromName(value);
            return true;
        }
        return false;
    }

    private static AzureDevOpsOrganization FromLegacyHost(string name)
        => new(name, $"https://{name}.visualstudio.com");

    private static string? SshPathAfter(string value, string hostWithColon)
    {
        var at = value.IndexOf(hostWithColon, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || value.Contains("://", StringComparison.Ordinal))
            return null;
        return value[(at + hostWithColon.Length)..];
    }

    /// <summary>組織名として使える文字だけか（英数字・ハイフン・アンダースコア・ピリオド）。</summary>
    private static bool IsName(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return false;
        return true;
    }
}
