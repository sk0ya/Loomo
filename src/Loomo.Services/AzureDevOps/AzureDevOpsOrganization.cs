using System;

namespace sk0ya.Loomo.Services;

/// <summary>
/// Azure DevOps の組織。REST の土台 URL と、資格情報を引くときのホスト・パスを持つ。
/// <para>旧形式（<c>{org}.visualstudio.com</c>）のリモートは、その形のまま扱う——Git Credential Manager は
/// ホストごとに資格情報を覚えるので、ホストを dev.azure.com へ読み替えると、git で通っているのに
/// Loomo だけサインインを求められる、ということが起きる。</para>
/// </summary>
public sealed record AzureDevOpsOrganization(string Name, string BaseUrl, string CredentialHost, string CredentialPath)
{
    /// <summary>dev.azure.com 形式の組織。</summary>
    public static AzureDevOpsOrganization FromName(string name)
        => new(name, $"https://dev.azure.com/{name}", "dev.azure.com", name);

    private static AzureDevOpsOrganization FromLegacyHost(string name)
        => new(name, $"https://{name}.visualstudio.com", $"{name}.visualstudio.com", "");

    /// <summary>git のリモート URL から組織を読む。対応する形：
    /// <list type="bullet">
    /// <item><c>https://[user@]dev.azure.com/{org}/{project}/_git/{repo}</c></item>
    /// <item><c>https://{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}</c></item>
    /// <item><c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c></item>
    /// <item><c>{org}@vs-ssh.visualstudio.com:v3/{org}/{project}/{repo}</c></item>
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

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
        return false;
    }

    /// <summary>人が設定に書いた値から組織を読む。リモート URL・組織 URL・組織名だけ、のどれでもよい。</summary>
    public static bool TryParseUserInput(string? text, out AzureDevOpsOrganization organization)
    {
        organization = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var value = text.Trim().TrimEnd('/');
        if (TryParseRemote(value, out organization))
            return true;
        // スキームを省いた「dev.azure.com/org」も受ける。
        if (!value.Contains("://", StringComparison.Ordinal) && value.Contains('.', StringComparison.Ordinal)
            && TryParseRemote("https://" + value, out organization))
            return true;
        if (IsName(value))
        {
            organization = FromName(value);
            return true;
        }
        return false;
    }

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
