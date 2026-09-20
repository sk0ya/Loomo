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
