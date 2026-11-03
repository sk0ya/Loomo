using System;

namespace sk0ya.Loomo.Services;

/// <summary>git のリモートが指す Azure DevOps のリポジトリ（REST を投げる先）。</summary>
public sealed record AzureDevOpsRemoteTarget(string OrganizationUrl, string Project, string Repository)
{
    public AzureDevOpsPrTarget PrTarget => new(Project, Repository);
}

/// <summary>
/// git のリモートの URL → Azure DevOps の組織・プロジェクト・リポジトリ（純粋）。PR のコメントをエディタに出すとき、
/// 手元のワークツリーがどの PR に当たるかを、そのリモートから引くのに使う。
/// <para>HTTPS（<c>https://org@dev.azure.com/org/proj/_git/repo</c>・<c>org.visualstudio.com</c>・Server の <c>/tfs/</c>）は
/// Web の URL と同じ形なので <see cref="AzureDevOpsWebUrl.TryParse"/> に任せ、頭のユーザー名だけ外す。SSH
/// （<c>git@ssh.dev.azure.com:v3/org/proj/repo</c>・<c>org@vs-ssh.visualstudio.com:v3/org/proj/repo</c>）はここで読む。</para>
/// </summary>
public static class AzureDevOpsRemote
{
    public static AzureDevOpsRemoteTarget? TryParse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;
        var url = remoteUrl.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // SSH：…:v3/組織/プロジェクト/リポジトリ（ssh:// の形も同じ並び）。
        var v3 = url.IndexOf("v3/", StringComparison.OrdinalIgnoreCase);
        if (v3 >= 0 && (url.Contains("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("vs-ssh.visualstudio.com", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = url[(v3 + 3)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return null;
            var organization = Uri.UnescapeDataString(parts[0]);
            var organizationUrl = url.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase)
                ? $"https://{organization}.visualstudio.com"
                : $"https://dev.azure.com/{organization}";
            return new AzureDevOpsRemoteTarget(organizationUrl, Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        // https://org@dev.azure.com/… の org@ は認証の名残。組織の URL に混ぜない。
        var withoutUser = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
        return AzureDevOpsWebUrl.TryParse(withoutUser) is { Repository.Length: > 0 } page
            ? new AzureDevOpsRemoteTarget(page.OrganizationUrl, page.Project, page.Repository)
            : null;
    }
}
