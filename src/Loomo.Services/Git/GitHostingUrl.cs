using System;

namespace sk0ya.Loomo.Services;

/// <summary>GitリモートURLをホスティングサービスのWeb URLへ変換する。</summary>
public static class GitHostingUrl
{
    /// <summary>GitHub.comのHTTPS/SSHリモートからリポジトリURLを作る。</summary>
    public static bool TryGetGitHubRepositoryUrl(string? remoteUrl, out string repositoryUrl)
    {
        repositoryUrl = "";
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return false;

        var value = remoteUrl.Trim();
        string? owner;
        string? repository;
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            (owner, repository) = SplitPath(value["git@github.com:".Length..]);
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                 && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                 && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase)))
        {
            (owner, repository) = SplitPath(uri.AbsolutePath);
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
            return false;

        repositoryUrl = $"https://github.com/{owner}/{repository}";
        return true;
    }

    /// <summary>
    /// Azure Repos のリモートから、プロジェクトとリポジトリの名前を読む（§24.93）。組織（URL）は設定の側を使うので読まない。
    /// HTTPS は <c>…/{project}/_git/{repo}</c>（dev.azure.com・visualstudio.com・Azure DevOps Server の <c>/tfs/{collection}/</c> も同じ形。
    /// プロジェクトと同じ名前のリポジトリは <c>…/{org}/_git/{repo}</c> と略されることがあり、そのときはリポジトリ名をプロジェクトとする）、
    /// SSH は <c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c> と <c>{org}@vs-ssh.visualstudio.com:v3/{org}/{project}/{repo}</c>。
    /// </summary>
    public static bool TryGetAzureReposTarget(string? remoteUrl, out string project, out string repository)
    {
        project = repository = "";
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return false;
        var value = remoteUrl.Trim();
        var v3 = value.IndexOf(":v3/", StringComparison.OrdinalIgnoreCase);
        if (v3 > 0 && (value.Contains("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("vs-ssh.visualstudio.com", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = value[(v3 + 4)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return false;
            project = Uri.UnescapeDataString(parts[1]);
            repository = Uri.UnescapeDataString(parts[2]);
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var git = Array.FindIndex(segments, s => string.Equals(s, "_git", StringComparison.OrdinalIgnoreCase));
        if (git < 0 || git + 1 >= segments.Length)
            return false;
        repository = Uri.UnescapeDataString(segments[git + 1]);
        // dev.azure.com/{org}/_git/{repo} の略記では _git の前が組織。visualstudio.com は組織がホスト名なので _git の前が無いこともある。
        var orgOnly = string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase) ? git == 1 : git == 0;
        project = orgOnly ? repository : Uri.UnescapeDataString(segments[git - 1]);
        return true;
    }

    private static (string? Owner, string? Repository) SplitPath(string path)
    {
        var parts = path.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return (null, null);

        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        return (parts[0], repository);
    }
}
