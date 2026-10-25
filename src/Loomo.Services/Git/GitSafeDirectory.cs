using System;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 所有者が違うために git が拒否したリポジトリ（<c>detected dubious ownership</c>・§24.91）。純関数。
/// git 2.35.2 以降は、今のユーザーが持ち主でないフォルダーのリポジトリを <c>safe.directory</c> に足すまで扱わない。
/// 外付けドライブ・別ユーザーで clone したもの・WSL のリポジトリで起きる。
/// </summary>
public static class GitSafeDirectory
{
    private static readonly Regex RepositoryAt = new(@"detected dubious ownership in repository at '(?<path>[^']+)'",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// git の stderr から、<c>safe.directory</c> に足すべきパスを読む。拒否の知らせでなければ null。
    /// git が勧める <c>git config --global --add safe.directory &lt;path&gt;</c> の行があればその表記をそのまま使う
    /// （git が照合に使う表記はそれ。無ければ「repository at '…'」のパス）。
    /// </summary>
    public static string? ParseDubiousOwnership(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return null;
        var at = RepositoryAt.Match(stderr);
        if (!at.Success)
            return null;
        const string marker = "safe.directory ";
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf("--add " + marker, StringComparison.Ordinal);
            if (i < 0)
                continue;
            var path = Unquote(line[(i + "--add ".Length + marker.Length)..].Trim());
            if (path.Length > 0)
                return path;
        }
        return at.Groups["path"].Value;
    }

    /// <summary>足すときの引数（<c>config --global --add safe.directory &lt;path&gt;</c>）。</summary>
    public static string[] TrustArguments(string path) => ["config", "--global", "--add", "safe.directory", path];

    private static string Unquote(string value)
        => value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"')
            ? value[1..^1]
            : value;
}
