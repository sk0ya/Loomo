using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

internal enum GitBranchOperation
{
    ForcePush,
    DeleteRemote,
    SetUpstream,
    UnsetUpstream,
    ShowLog,
    Checkout,
    Merge,
    MergeFastForwardOnly,
    MergeNoFastForward,
    MergeSquash,
    Rebase,
    CreateFrom,
    Pull,
    Push,
    Delete,
}

internal enum GitTagOperation { Checkout, Push, Delete, PushAll }
internal enum GitRemoteOperation { Add, SetUrl, Remove }
internal enum GitSubmoduleOperation { Init, Update, Sync }
internal enum GitCommitOperation
{
    CreateBranch,
    Checkout,
    RewriteMessage,
    OpenFileRevision,
    CompareFileRevision,
    RestoreFileRevision,
    InteractiveRebase,
    Squash,
    CherryPick,
    CherryPickNoCommit,
    Revert,
    RevertNoCommit,
    ResetSoft,
    ResetMixed,
    ResetHard,
    OpenPatch,
}

internal enum GitConfirmationSeverity { Question, Warning }

internal sealed record GitOperationPrompt(
    string Title,
    string Message,
    string InitialValue = "",
    bool AllowEmpty = false,
    bool Multiline = false);

internal sealed record GitOperationConfirmation(
    string Title,
    string Message,
    GitConfirmationSeverity Severity = GitConfirmationSeverity.Question);

/// <summary>
/// Git ペインのコマンド選択・入力検証・実行順をまとめる。ダイアログ表示だけ callback として View に渡す。
/// </summary>
internal static class GitSessionOperationController
{
    internal static async Task ForcePushCurrentBranchAsync(
        GitSessionViewModel? vm, Func<string, bool> confirm)
    {
        if (vm is null)
            return;

        var target = GitBranchActionPolicy.ForcePushTarget(vm.UpstreamLabel);
        if (confirm(target))
            await vm.PushForceAsync();
    }

    internal static Task PullWithModeAsync(GitSessionViewModel? vm, GitPullMode mode)
        => vm?.PullWithModeAsync(mode) ?? Task.CompletedTask;

    internal static async Task ExecuteBranchAsync(
        GitSessionViewModel? vm,
        GitBranchInfo? branch,
        GitBranchOperation operation,
        Func<string, bool>? confirmForcePush = null,
        Func<string, bool>? confirmRemoteDelete = null,
        Func<GitSessionViewModel, GitBranchInfo, string?>? promptUpstream = null,
        Func<GitOperationPrompt, string?>? prompt = null,
        Func<GitOperationConfirmation, bool>? confirm = null)
    {
        if (vm is null)
            return;

        if (operation == GitBranchOperation.Push || operation == GitBranchOperation.DeleteRemote
            || operation == GitBranchOperation.UnsetUpstream)
        {
            if (branch is null)
                return;
            switch (operation)
            {
                case GitBranchOperation.Push:
                    await vm.PushBranchAsync(branch);
                    return;
                case GitBranchOperation.DeleteRemote:
                    if (branch.IsRemote && confirmRemoteDelete?.Invoke(branch.Name) == true)
                        await vm.DeleteRemoteBranchAsync(branch);
                    return;
                case GitBranchOperation.UnsetUpstream:
                    await vm.UnsetUpstreamAsync(branch);
                    return;
            }
        }

        if (operation == GitBranchOperation.ForcePush)
        {
            if (branch is not null && confirmForcePush?.Invoke(branch.Name) == true)
                await vm.PushBranchAsync(branch, force: true);
            return;
        }

        if (branch is null)
            return;

        switch (operation)
        {
            case GitBranchOperation.SetUpstream:
                var upstream = promptUpstream?.Invoke(vm, branch);
                if (!string.IsNullOrWhiteSpace(upstream))
                    await vm.SetUpstreamAsync(branch, upstream);
                break;
            case GitBranchOperation.ShowLog:
                await vm.ShowBranchLogAsync(branch);
                break;
            case GitBranchOperation.Checkout:
                await vm.Commands.CheckoutBranchAsync(branch);
                break;
            case GitBranchOperation.Merge:
                await vm.Commands.MergeAsync(branch);
                break;
            case GitBranchOperation.MergeFastForwardOnly:
                await vm.Commands.MergeAsync(branch, GitMergeStrategy.FastForwardOnly);
                break;
            case GitBranchOperation.MergeNoFastForward:
                await vm.Commands.MergeAsync(branch, GitMergeStrategy.NoFastForward);
                break;
            case GitBranchOperation.MergeSquash:
                await vm.Commands.MergeAsync(branch, GitMergeStrategy.Squash);
                break;
            case GitBranchOperation.Rebase:
                if (confirm?.Invoke(new(
                    "リベース",
                    $"現在のブランチを {branch.Name} の上へリベースします。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？")) == true)
                    await vm.Commands.RebaseAsync(branch);
                break;
            case GitBranchOperation.CreateFrom:
                var branchName = prompt?.Invoke(new(
                    "新しいブランチ", $"{branch.Name} から作成するブランチ名を入力してください"));
                if (!string.IsNullOrWhiteSpace(branchName))
                    await vm.Commands.CreateBranchAsync(branchName, branch.Name);
                break;
            case GitBranchOperation.Pull:
                await vm.PullBranchAsync(branch);
                break;
            case GitBranchOperation.Delete:
                await GitBranchDeletionCoordinator.DeleteWithForceFallbackAsync(
                    vm.Commands, branch,
                    name => confirm?.Invoke(new("ブランチ削除", $"ブランチ {name} を削除しますか？",
                        GitConfirmationSeverity.Warning)) == true,
                    name => confirm?.Invoke(new("ブランチの強制削除",
                        $"{name} はマージされていないコミットを含みます。強制削除（-D）しますか？\nコミットが失われる可能性があります。",
                        GitConfirmationSeverity.Warning)) == true);
                break;
        }
    }

    internal static async Task CreateBranchAsync(
        GitSessionViewModel? vm,
        GitBranchInfo? start,
        Func<GitOperationPrompt, string?> prompt)
    {
        if (vm is null)
            return;
        var message = start is null
            ? "ブランチ名を入力してください"
            : $"{start.Name} から作成するブランチ名を入力してください";
        var name = prompt(new("新しいブランチ", message));
        if (!string.IsNullOrWhiteSpace(name))
            await vm.Commands.CreateBranchAsync(name, start?.Name);
    }

    internal static async Task ExecuteTagAsync(
        GitSessionViewModel? vm,
        GitTagInfo? tag,
        GitTagOperation operation,
        Func<GitOperationConfirmation, bool>? confirm = null)
    {
        if (vm is null)
            return;
        switch (operation)
        {
            case GitTagOperation.Checkout when tag is not null:
                await vm.Commands.CheckoutTagAsync(tag);
                break;
            case GitTagOperation.Push when tag is not null:
                await vm.Commands.PushTagAsync(tag);
                break;
            case GitTagOperation.Delete when tag is not null:
                if (confirm?.Invoke(new("タグ削除", $"タグ {tag.Name} を削除しますか？",
                    GitConfirmationSeverity.Warning)) == true)
                    await vm.Commands.DeleteTagAsync(tag);
                break;
            case GitTagOperation.PushAll:
                await vm.Commands.PushAllTagsAsync();
                break;
        }
    }

    internal static async Task CreateTagAsync(
        GitSessionViewModel? vm,
        string? target,
        Func<GitOperationPrompt, string?> prompt)
    {
        if (vm is null)
            return;
        var name = prompt(new("タグを作成", "タグ名を入力してください"));
        if (string.IsNullOrWhiteSpace(name))
            return;
        var message = prompt(new("タグを作成", "注釈メッセージ（空なら軽量タグ）:", AllowEmpty: true));
        if (message is not null)
            await vm.Commands.CreateTagAsync(name, target, message);
    }

    internal static async Task ExecuteSubmoduleAsync(
        GitSessionViewModel? vm,
        GitSubmoduleInfo? submodule,
        GitSubmoduleOperation operation)
    {
        if (vm is null)
            return;
        switch (operation)
        {
            case GitSubmoduleOperation.Init when submodule is not null:
                await vm.Commands.InitSubmoduleAsync(submodule);
                break;
            case GitSubmoduleOperation.Update when submodule is not null:
                await vm.Commands.UpdateSubmoduleAsync(submodule);
                break;
            case GitSubmoduleOperation.Sync:
                await vm.Commands.SyncSubmodulesAsync();
                break;
        }
    }

    internal static async Task ExecuteRemoteAsync(
        GitSessionViewModel? vm,
        GitRemoteInfo? remote,
        GitRemoteOperation operation,
        Func<GitOperationPrompt, string?>? prompt = null,
        Func<GitOperationConfirmation, bool>? confirm = null)
    {
        if (vm is null)
            return;
        switch (operation)
        {
            case GitRemoteOperation.Add:
                var initialName = vm.Remotes.Count == 0 ? "origin" : "";
                var name = prompt?.Invoke(new("リモートを追加", "リモート名を入力してください（例: origin）", initialName));
                if (string.IsNullOrWhiteSpace(name))
                    return;
                var url = prompt?.Invoke(new("リモートを追加", $"{name} の URL を入力してください"));
                if (!string.IsNullOrWhiteSpace(url))
                    await vm.AddRemoteAsync(name, url);
                break;
            case GitRemoteOperation.SetUrl when remote is not null:
                var newUrl = prompt?.Invoke(new("リモートの URL を変更", $"{remote.Name} の URL", remote.Url));
                if (!string.IsNullOrWhiteSpace(newUrl) && newUrl != remote.Url)
                    await vm.SetRemoteUrlAsync(remote.Name, newUrl);
                break;
            case GitRemoteOperation.Remove when remote is not null:
                if (confirm?.Invoke(new("リモートの削除",
                    $"リモート {remote.Name}（{remote.Url}）を削除しますか？\n" +
                    "追跡ブランチと上流の設定も一緒に消えます（リモート側のリポジトリはそのままです）。",
                    GitConfirmationSeverity.Warning)) == true)
                    await vm.RemoveRemoteAsync(remote.Name);
                break;
        }
    }

    internal static async Task ExecuteCommitAsync(
        GitSessionViewModel? vm,
        GitLogRow? row,
        GitCommitOperation operation,
        IReadOnlyList<GitLogRow>? selectedRows = null,
        Func<GitOperationPrompt, string?>? prompt = null,
        Func<GitOperationConfirmation, bool>? confirm = null,
        Func<IReadOnlyList<RebasePlanEntry>, (IReadOnlyList<RebasePlanEntry> Plan,
            IReadOnlyDictionary<string, string> Messages)?>? showRebasePlan = null,
        Action<string>? showError = null)
    {
        if (vm is null)
            return;
        switch (operation)
        {
            case GitCommitOperation.CreateBranch when row is not null:
                var branchName = prompt?.Invoke(new("新しいブランチ",
                    $"コミット {row.ShortHash} から作成するブランチ名を入力してください"));
                if (!string.IsNullOrWhiteSpace(branchName))
                    await vm.Commands.CreateBranchAsync(branchName, row.Hash);
                break;
            case GitCommitOperation.Checkout when row is not null:
                await vm.Commands.CheckoutCommitAsync(row);
                break;
            case GitCommitOperation.RewriteMessage when row is not null:
                var current = await vm.Commands.GetCommitMessageAsync(row);
                var message = prompt?.Invoke(new("コミットメッセージを修正",
                    $"{row.ShortHash} のコミットメッセージを入力してください。\nこのコミット以降の履歴が書き換わります。",
                    current, Multiline: true));
                if (message is null || string.Equals(message, current, StringComparison.Ordinal))
                    return;
                if (confirm?.Invoke(new("コミットメッセージを修正",
                    $"{row.ShortHash} 以降のコミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
                    GitConfirmationSeverity.Warning)) == true)
                    await vm.Commands.RewriteCommitMessageAsync(row, message);
                break;
            case GitCommitOperation.OpenFileRevision when row is not null:
                await vm.OpenFileAtRevisionAsync(row);
                break;
            case GitCommitOperation.CompareFileRevision when row is not null:
                await vm.CompareFileWithRevisionAsync(row);
                break;
            case GitCommitOperation.RestoreFileRevision when row is not null:
                if (confirm?.Invoke(new("この版の内容へ戻す",
                    $"{vm.History.ScopedPath} を {row.ShortHash} の時点の内容へ戻します。\n" +
                    "作業ツリーの現在の内容は失われます（履歴は書き換えません）。\n\n実行しますか？",
                    GitConfirmationSeverity.Warning)) == true)
                    await vm.RestoreFileAtRevisionAsync(row);
                break;
            case GitCommitOperation.InteractiveRebase when row is not null:
                var (entries, error) = await vm.Commands.GetRebaseCandidatesAsync(row);
                if (error is not null)
                {
                    showError?.Invoke(error);
                    return;
                }
                if (confirm?.Invoke(new("インタラクティブリベース",
                    $"{row.ShortHash} から HEAD までの履歴が書き換わります。実行しますか？")) != true)
                    return;
                var plan = showRebasePlan?.Invoke(entries);
                if (plan is not null)
                    await vm.Commands.InteractiveRebaseAsync(row.Hash!, plan.Value.Plan, plan.Value.Messages);
                break;
            case GitCommitOperation.Squash:
                var rows = selectedRows ?? Array.Empty<GitLogRow>();
                if (rows.Count < 2)
                    return;
                var combinedMessage = await vm.GetCombinedCommitMessageAsync(rows);
                var squashMessage = prompt?.Invoke(new("スカッシュ後のコミットメッセージ",
                    "スカッシュ後に使用するコミットメッセージを編集してください。",
                    combinedMessage, Multiline: true));
                if (squashMessage is null)
                    return;
                if (confirm?.Invoke(new("スカッシュ",
                    $"選択した {rows.Count} 件のコミットを1つにまとめます。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？")) == true)
                    await vm.Commands.SquashAsync(rows, squashMessage);
                break;
            case GitCommitOperation.CherryPick when row is not null:
                await vm.Commands.CherryPickAsync(row);
                break;
            case GitCommitOperation.CherryPickNoCommit when row is not null:
                await vm.Commands.CherryPickNoCommitAsync(row);
                break;
            case GitCommitOperation.Revert when row is not null:
                await vm.Commands.RevertAsync(row);
                break;
            case GitCommitOperation.RevertNoCommit when row is not null:
                await vm.Commands.RevertNoCommitAsync(row);
                break;
            case GitCommitOperation.ResetSoft when row is not null:
                await vm.Commands.ResetAsync(row, GitResetMode.Soft);
                break;
            case GitCommitOperation.ResetMixed when row is not null:
                await vm.Commands.ResetAsync(row, GitResetMode.Mixed);
                break;
            case GitCommitOperation.ResetHard when row is not null:
                if (confirm?.Invoke(new("リセット (hard)",
                    $"{row.ShortHash} まで hard リセットします。作業ツリー・インデックスの変更はすべて失われます。\n実行しますか？",
                    GitConfirmationSeverity.Warning)) == true)
                    await vm.Commands.ResetAsync(row, GitResetMode.Hard);
                break;
            case GitCommitOperation.OpenPatch when row is not null:
                await vm.OpenPatchAsync(row);
                break;
        }
    }

    internal static async Task CopyCommitPatchAsync(
        GitSessionViewModel? vm,
        GitLogRow? row,
        Action<string?> copyText)
    {
        if (vm is null || row is null)
            return;

        copyText(await vm.GetCommitPatchAsync(row));
    }
}
