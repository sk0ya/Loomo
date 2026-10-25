using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ブランチを片付けた結果（§24.69）。<see cref="Failed"/> は「名前: 理由」。</summary>
public sealed record GitBranchCleanupOutcome(int Deleted, IReadOnlyList<string> Failed);

public sealed partial class GitSessionViewModel
{
    // ===== ブランチの片付け（§24.69） =====

    /// <summary>ローカルブランチ（片付けの窓に並べるもの）。</summary>
    public IReadOnlyList<GitBranchInfo> LocalBranches => _allBranches.Where(b => !b.IsRemote).ToList();

    /// <summary>このブランチにオープンな PR があるか（§24.39 で引いたもの。GitHub でなければ常に false）。</summary>
    public bool HasOpenPullRequest(string branch) => _branchPullRequests.ContainsKey(branch);

    /// <summary>既定ブランチ（<c>origin/main</c> など）。決まらなければ null。</summary>
    public Task<string?> GetDefaultBranchAsync() => _git.GetDefaultBranchAsync();

    /// <summary>既定ブランチと、そこへ取り込み済みのローカルブランチ。</summary>
    public Task<(string? DefaultBranch, IReadOnlySet<string> Merged)> InspectBranchMergesAsync(
        IReadOnlyList<string> branches, CancellationToken cancellationToken)
        => _git.InspectBranchMergesAsync(branches, cancellationToken);

    /// <summary>選んだブランチを 1 本ずつ消す。<paramref name="force"/> でなければ <c>-d</c>（取り込まれていないものは git が断る）、
    /// なら <c>-D</c>。消し終えたら一覧を読み直す。</summary>
    public async Task<GitBranchCleanupOutcome> DeleteBranchesAsync(IReadOnlyList<GitBranchInfo> branches, bool force)
    {
        var deleted = 0;
        var failed = new List<string>();
        foreach (var branch in branches)
        {
            var result = await Commands.DeleteBranchAsync(branch, force);
            if (result is { Success: true })
                deleted++;
            else
                failed.Add($"{branch.Name}: {Reason(result)}");
        }
        await RefreshAsync();
        return new GitBranchCleanupOutcome(deleted, failed);

        static string Reason(GitCommandResult? result)
            => result is null ? "ほかの Git 操作の実行中でした"
                : GitBranchActionPolicy.RequiresForceDelete(result.Message) ? "取り込まれていないコミットがあります（「取り込まれていないものも消す」で -D）"
                : result.Message.Trim() is { Length: > 0 } message ? message.Split('\n')[0].Trim()
                : "削除できませんでした";
    }
}
