using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リモート、ブランチ、タグの参照情報を照会する。</summary>
public sealed class GitBranchService
{
    private readonly GitCommandRunner _runner;

    public GitBranchService(GitCommandRunner runner) => _runner = runner;

    public async Task<IReadOnlyList<string>> GetRemotesAsync()
    {
        var result = await _runner.RunAsync("remote").ConfigureAwait(false);
        return result.Success
            ? result.Output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList()
            : Array.Empty<string>();
    }

    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync()
    {
        var result = await _runner.RunAsync(
            "branch", "-a",
            "--format=%(refname)\t%(HEAD)\t%(upstream:short)\t%(upstream:track)\t%(committerdate:iso-strict)")
            .ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitBranchInfo>();

        var branches = new List<GitBranchInfo>();
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\t');
            if (parts.Length < 2) continue;

            var refName = parts[0];
            var upstream = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            var (ahead, behind, gone) = ParseTrack(parts.Length > 3 ? parts[3] : "");
            var lastCommit = ParseDate(parts.Length > 4 ? parts[4] : "");
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                branches.Add(new GitBranchInfo(
                    refName["refs/heads/".Length..], parts[1] == "*", IsRemote: false, upstream)
                {
                    Ahead = ahead,
                    Behind = behind,
                    UpstreamGone = gone,
                    LastCommit = lastCommit,
                });
            }
            else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = refName["refs/remotes/".Length..];
                if (name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                branches.Add(new GitBranchInfo(name, IsCurrent: false, IsRemote: true, upstream)
                {
                    LastCommit = lastCommit,
                });
            }
        }
        return branches;
    }

    public async Task<IReadOnlyList<GitTagInfo>> GetTagsAsync()
    {
        var result = await _runner.RunAsync("for-each-ref", "refs/tags", "--sort=-creatordate",
            "--format=%(refname:short)\t%(objecttype)\t%(objectname:short)\t%(*objectname:short)\t%(subject)\t%(creatordate:format:%Y-%m-%d %H:%M)")
            .ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitTagInfo>();

        var tags = new List<GitTagInfo>();
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\t');
            if (parts.Length < 6) continue;
            var isAnnotated = parts[1] == "tag";
            var target = isAnnotated && parts[3].Length > 0 ? parts[3] : parts[2];
            tags.Add(new GitTagInfo(parts[0], target,
                parts[4].Length > 0 ? parts[4] : null,
                isAnnotated,
                parts[5].Length > 0 ? parts[5] : null));
        }
        return tags;
    }

    internal static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track.Length == 0) return (0, 0, false);
        if (track.Contains("gone", StringComparison.Ordinal)) return (0, 0, true);
        return (ReadCount(track, "ahead "), ReadCount(track, "behind "), false);

        static int ReadCount(string value, string keyword)
        {
            var at = value.IndexOf(keyword, StringComparison.Ordinal);
            if (at < 0) return 0;
            var digits = value[(at + keyword.Length)..].TakeWhile(char.IsAsciiDigit).ToArray();
            return digits.Length > 0 && int.TryParse(digits, out var count) ? count : 0;
        }
    }

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
