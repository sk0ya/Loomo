using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブランチ行に対して有効な操作と、入力ダイアログの既定値を決める。</summary>
internal static class GitBranchActionPolicy
{
    public static BranchMenuAvailability ForBranch(GitBranchInfo branch, bool hasRemote)
    {
        var isLocal = !branch.IsRemote;
        var canPush = isLocal && hasRemote;
        return new(
            CanCheckout: !branch.IsCurrent,
            CanMerge: !branch.IsCurrent,
            CanRebase: !branch.IsCurrent,
            CanDelete: !branch.IsCurrent && isLocal,
            ShowRemoteDelete: branch.IsRemote,
            CanSetUpstream: isLocal && hasRemote,
            CanUnsetUpstream: isLocal && branch.Upstream is not null,
            CanPull: isLocal && branch.Upstream is not null && hasRemote,
            CanPush: canPush);
    }

    public static UpstreamPromptArguments UpstreamPrompt(
        string branchName,
        string? currentUpstream,
        string remoteLabel,
        IReadOnlyList<string> remoteBranchNames)
    {
        var initialValue = currentUpstream is { Length: > 0 } current
            ? current
            : remoteLabel.Length > 0 ? $"{remoteLabel}/{branchName}" : "";
        var candidates = remoteBranchNames
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .ToList();
        var hint = candidates.Count > 0
            ? "\n\n候補: " + string.Join(", ", candidates)
            : "";
        return new(initialValue, hint);
    }

    public static string ForcePushTarget(string upstreamLabel)
        => upstreamLabel.Length > 0 ? upstreamLabel : "現在のブランチ";

    public static bool RequiresForceDelete(string errorMessage)
        => errorMessage.Contains("not fully merged", StringComparison.OrdinalIgnoreCase);
}

internal sealed record BranchMenuAvailability(
    bool CanCheckout,
    bool CanMerge,
    bool CanRebase,
    bool CanDelete,
    bool ShowRemoteDelete,
    bool CanSetUpstream,
    bool CanUnsetUpstream,
    bool CanPull,
    bool CanPush);

internal sealed record UpstreamPromptArguments(string InitialValue, string CandidateHint);
