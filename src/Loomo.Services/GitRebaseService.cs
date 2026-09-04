using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リベース、履歴リセット、履歴書き換えを扱う。</summary>
public sealed class GitRebaseService
{
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitRebaseService(GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _runner = runner;
        _mutations = mutations;
    }

    public Task<GitCommandResult> RebaseAsync(string onto) =>
        _mutations.ExecuteAsync("rebase", onto);

    /// <summary>
    /// ステージ済みの変更を指定コミットへ追加する。HEAD なら通常の amend、過去のコミットなら
    /// 一時的な fixup コミットを作って autosquash する。未ステージの変更は一時退避し、成功後に戻す。
    /// </summary>
    public async Task<GitCommandResult> AmendCommitAsync(string targetHash, bool sign = false)
    {
        try
        {
            return await AmendCommitCoreAsync(targetHash, sign).ConfigureAwait(false);
        }
        finally
        {
            _mutations.NotifyRepositoryChanged();
        }
    }

    private async Task<GitCommandResult> AmendCommitCoreAsync(string targetHash, bool sign)
    {
        if (string.IsNullOrWhiteSpace(targetHash))
            return new GitCommandResult(-1, "", "amend 対象のコミットが選択されていません。");

        var resolved = await _runner.RunAsync(
            "rev-parse", "--verify", $"{targetHash}^{{commit}}").ConfigureAwait(false);
        if (!resolved.Success) return resolved;
        var target = resolved.Output.Trim();

        var headResult = await _runner.RunAsync("rev-parse", "HEAD").ConfigureAwait(false);
        if (!headResult.Success) return headResult;
        var head = headResult.Output.Trim();

        if (string.Equals(target, head, StringComparison.OrdinalIgnoreCase))
        {
            var args = new List<string> { "commit", "--amend", "--no-edit" };
            if (sign) args.Add("-S");
            return await _mutations.ExecuteAsync(args.ToArray()).ConfigureAwait(false);
        }

        var onHead = await _runner.RunAsync("merge-base", "--is-ancestor", target, "HEAD")
            .ConfigureAwait(false);
        // 「祖先ではない」は exit 1 だけ。それ以外（壊れたリポジトリ・不正なオブジェクト等）を
        // 同じ文言に丸めると、git のエラーを捨てて範囲制限として報告してしまう。
        if (onHead.ExitCode != 0)
            return onHead.ExitCode == 1
                ? new GitCommandResult(-1, "", "現在のブランチに含まれるコミットのみ amend できます。")
                : onHead;

        var hasParent = (await _runner.RunAsync(
            "rev-parse", "--verify", "--quiet", $"{target}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{target}^..HEAD" : "HEAD";
        var chainResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success) return chainResult;
        var chain = SplitLines(chainResult.Output);
        if (chain.Count == 0 || !string.Equals(chain[0], target, StringComparison.OrdinalIgnoreCase))
            return new GitCommandResult(-1, "", "現在のブランチの主系列にあるコミットのみ amend できます。");

        var merges = await _runner.RunAsync("rev-list", "--min-parents=2", range)
            .ConfigureAwait(false);
        if (!merges.Success) return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "", "対象から HEAD までにマージコミットがあるため amend できません。");

        var stagedCheck = await _runner.RunAsync("diff", "--cached", "--quiet").ConfigureAwait(false);
        if (stagedCheck.ExitCode != 1)
            return stagedCheck.Success
                ? new GitCommandResult(-1, "", "対象コミットへ追加する変更がステージされていません。")
                : stagedCheck;

        var unstagedCheck = await _runner.RunAsync("diff", "--quiet").ConfigureAwait(false);
        if (unstagedCheck.ExitCode is not (0 or 1)) return unstagedCheck;
        var untracked = await _runner.RunAsync("ls-files", "--others", "--exclude-standard")
            .ConfigureAwait(false);
        if (!untracked.Success) return untracked;

        string? previousStash = null;
        var stashNeeded = unstagedCheck.ExitCode == 1 || !string.IsNullOrWhiteSpace(untracked.Output);
        if (stashNeeded)
        {
            previousStash = (await _runner.RunAsync(
                "stash", "list", "-1", "--format=%H").ConfigureAwait(false)).Output.Trim();
            // 内部手順は _runner で回す。_mutations は毎回 RepositoryChanged を上げるので、
            // rebase 実行中に Git パネルが更新され、amend 候補が detached HEAD から読み直されて
            // 選択が勝手に移る（操作ログにも Loomo 内部の stash が利用者の操作として並ぶ）。
            // 完了通知は AmendCommitAsync の finally が1度だけ出す。
            var stash = await _runner.RunAsync(
                "stash", "push", "--keep-index", "-u", "-m", "Loomo: amend 前の未ステージ変更")
                .ConfigureAwait(false);
            if (!stash.Success) return stash;
        }

        // <c>--fixup=&lt;sha&gt;</c> が書くメッセージは「fixup! <b>対象の件名</b>」で、autosquash はまず
        // 件名で対象を探す——範囲内に同じ件名のコミットが2つあると、せっかく解決した SHA が捨てられて
        // 別のコミットへ静かに吸い込まれる。git は「fixup! 」の後ろがコミット名ならそれで引くので、
        // 解決済みのハッシュをそのまま書いて曖昧さを残さない。
        var fixupArgs = new List<string> { "commit", "-m", $"fixup! {target}" };
        if (sign) fixupArgs.Add("-S");
        var fixup = await _runner.RunAsync(fixupArgs.ToArray()).ConfigureAwait(false);
        if (!fixup.Success)
        {
            var stashNote = stashNeeded
                ? await RestoreAmendStashAsync(previousStash).ConfigureAwait(false)
                : "";
            return WithNote(fixup, stashNote);
        }

        // 署名は fixup コミットではなく rebase 側に要る。fixup コミットは squash されて消え、
        // 手元に残るのは rebase が作り直した対象コミットとその後続——ここに -S が無いと、
        // 署名にチェックを入れていても書き換えた範囲が丸ごと未署名になる。
        var baseArgument = hasParent ? $"{target}^" : "--root";
        var rebaseArgs = new List<string> { "rebase", "-i", "--autosquash" };
        if (sign) rebaseArgs.Add("-S");
        rebaseArgs.Add(baseArgument);
        var rebase = await _runner.RunAsync(rebaseArgs.ToArray()).ConfigureAwait(false);
        if (!rebase.Success)
            return WithNote(rebase,
                await RollbackFailedAmendAsync(head, stashNeeded, previousStash).ConfigureAwait(false));

        if (stashNeeded)
        {
            var restore = await _runner.RunAsync("stash", "pop").ConfigureAwait(false);
            // 履歴の書き換えはもう済んでいる。ここで失敗を返すと UI は amend 自体が失敗したと表示し、
            // amend の入力状態も戻らない——成功として返し、退避の回収方法だけ添える。
            if (!restore.Success)
                return new GitCommandResult(0, rebase.Output,
                    "amend は完了しましたが、退避した未ステージの変更を戻せませんでした"
                    + $"（git stash pop で戻せます）。{Environment.NewLine}{restore.Message}");
        }
        return rebase;
    }

    /// <summary>
    /// autosquash が失敗したときに amend 前の状態へ戻す。リベースを中断し、squash されずに残った
    /// fixup コミットを畳み（ステージ済みの内容はインデックスへ戻る）、退避した変更を戻す。
    /// 何もせずに返すと、進行中のリベース・宙に浮いた fixup コミット・黙って作った stash が
    /// そのまま残る——戻しきれなかったぶんは、利用者が自分で回収できるよう文言にして返す。
    /// </summary>
    private async Task<string> RollbackFailedAmendAsync(
        string originalHead, bool stashNeeded, string? previousStash)
    {
        var notes = new List<string>();
        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is not null && IsRebaseInProgress(gitDirectory))
            await _runner.RunAsync("rebase", "--abort").ConfigureAwait(false);

        var headNow = (await _runner.RunAsync("rev-parse", "HEAD").ConfigureAwait(false)).Output.Trim();
        if (!string.Equals(headNow, originalHead, StringComparison.OrdinalIgnoreCase))
        {
            var parent = (await _runner.RunAsync("rev-parse", "HEAD^").ConfigureAwait(false)).Output.Trim();
            var dropped = string.Equals(parent, originalHead, StringComparison.OrdinalIgnoreCase)
                && (await _runner.RunAsync("reset", "--soft", "HEAD^").ConfigureAwait(false)).Success;
            if (!dropped)
                notes.Add($"一時的な fixup コミットが残っています（git reset --soft {originalHead} で戻せます）。");
        }

        if (stashNeeded)
        {
            var stashNote = await RestoreAmendStashAsync(previousStash).ConfigureAwait(false);
            if (stashNote.Length > 0) notes.Add(stashNote);
        }
        return notes.Count == 0 ? "" : string.Join(Environment.NewLine, notes);
    }

    /// <summary>退避した未ステージ変更を戻す。戻せなかったときだけ、その旨の文言を返す。</summary>
    private async Task<string> RestoreAmendStashAsync(string? previousStash)
    {
        var current = (await _runner.RunAsync(
            "stash", "list", "-1", "--format=%H").ConfigureAwait(false)).Output.Trim();
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, previousStash, StringComparison.Ordinal))
            return "";
        var pop = await _runner.RunAsync("stash", "pop").ConfigureAwait(false);
        return pop.Success
            ? ""
            : "退避した未ステージの変更は stash に残っています（git stash pop で戻せます）。";
    }

    /// <summary>失敗結果へ後始末の顛末を書き足す（表示は stderr 優先なので Error 側へ寄せる）。</summary>
    private static GitCommandResult WithNote(GitCommandResult result, string note) =>
        note.Length == 0
            ? result
            : result with { Error = $"{result.Message}{Environment.NewLine}{note}" };

    public async Task<GitCommandResult> ContinueAsync()
    {
        var result = await _mutations.ExecuteAsync("rebase", "--continue").ConfigureAwait(false);
        if (result.Success)
            await DeleteScriptedArtifactsAsync().ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> SkipAsync() =>
        _mutations.ExecuteAsync("rebase", "--skip");

    public async Task<GitCommandResult> AbortAsync()
    {
        var result = await _mutations.ExecuteAsync("rebase", "--abort").ConfigureAwait(false);
        await DeleteScriptedArtifactsAsync().ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> ResetAsync(string hash, GitResetMode mode) =>
        _mutations.ExecuteAsync("reset", $"--{mode.ToString().ToLowerInvariant()}", hash);

    public async Task<string> GetCommitMessageAsync(string hash, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            null, cancellationToken, "show", "-s", "--format=%B", hash).ConfigureAwait(false);
        return result.Success ? result.Output.TrimEnd('\r', '\n') : "";
    }

    public async Task<GitCommandResult> RewriteCommitMessageAsync(string hash, string message)
    {
        try
        {
            return await RewriteCommitMessageCoreAsync(hash, message).ConfigureAwait(false);
        }
        finally
        {
            _mutations.NotifyRepositoryChanged();
        }
    }

    private async Task<GitCommandResult> RewriteCommitMessageCoreAsync(string hash, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommandResult(-1, "", "コミットメッセージを入力してください。");

        var onHead = await _runner.RunAsync("merge-base", "--is-ancestor", hash, "HEAD")
            .ConfigureAwait(false);
        if (onHead.ExitCode != 0)
            return onHead.ExitCode == 1
                ? new GitCommandResult(-1, "", "現在のブランチに含まれるコミットのみ修正できます。")
                : onHead;

        var hasParent = (await _runner.RunAsync(
            "rev-parse", "--verify", "--quiet", $"{hash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{hash}^..HEAD" : "HEAD";
        var chainResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success) return chainResult;
        var chain = SplitLines(chainResult.Output);
        if (chain.Count == 0 || !string.Equals(chain[0], hash, StringComparison.OrdinalIgnoreCase))
            return new GitCommandResult(-1, "", "現在のブランチの主系列にあるコミットのみ修正できます。");

        var merges = await _runner.RunAsync("rev-list", "--min-parents=2", range)
            .ConfigureAwait(false);
        if (!merges.Success) return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "", "対象から HEAD までにマージコミットがあるため、メッセージを修正できません。");

        var todo = new StringBuilder().Append("reword ").Append(chain[0]).Append('\n');
        foreach (var commit in chain.Skip(1))
            todo.Append("pick ").Append(commit).Append('\n');

        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var todoPath = Path.Combine(gitDirectory, "loomo-reword-todo.txt");
        var messagePath = Path.Combine(gitDirectory, "loomo-reword-message.txt");
        try
        {
            await File.WriteAllTextAsync(todoPath, todo.ToString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                messagePath, message.TrimEnd() + Environment.NewLine).ConfigureAwait(false);
            var environment = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp '{ToMsysPath(todoPath)}'",
                ["GIT_EDITOR"] = $"cp '{ToMsysPath(messagePath)}'",
            };
            return await _runner.RunAsync(
                environment, "rebase", "-i", hasParent ? $"{hash}^" : "--root").ConfigureAwait(false);
        }
        finally
        {
            TryDelete(todoPath);
            TryDelete(messagePath);
        }
    }

    public async Task<GitCommandResult> SquashAsync(
        IReadOnlyList<string> hashes, string? commitMessage = null)
    {
        try
        {
            return await SquashCoreAsync(hashes, commitMessage).ConfigureAwait(false);
        }
        finally
        {
            _mutations.NotifyRepositoryChanged();
        }
    }

    private async Task<GitCommandResult> SquashCoreAsync(
        IReadOnlyList<string> hashes, string? commitMessage)
    {
        if (hashes.Count < 2)
            return new GitCommandResult(-1, "", "スカッシュには2件以上のコミットを選択してください。");
        if (commitMessage is not null && string.IsNullOrWhiteSpace(commitMessage))
            return new GitCommandResult(-1, "", "コミットメッセージを入力してください。");

        var resolved = new List<string>(hashes.Count);
        foreach (var hash in hashes)
        {
            var result = await _runner.RunAsync(
                "rev-parse", "--verify", $"{hash}^{{commit}}").ConfigureAwait(false);
            if (!result.Success) return result;
            resolved.Add(result.Output.Trim());
        }
        var selected = resolved.ToHashSet(StringComparer.Ordinal);
        if (selected.Count < 2)
            return new GitCommandResult(-1, "", "スカッシュには2件以上のコミットを選択してください。");

        var historyResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", "HEAD").ConfigureAwait(false);
        if (!historyResult.Success) return historyResult;
        var selectedHistory = historyResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(selected.Contains).ToList();
        if (selectedHistory.Count != selected.Count)
            return new GitCommandResult(-1, "", "現在のブランチに含まれる連続したコミットのみスカッシュできます。");
        var oldest = selectedHistory[0];
        var newest = selectedHistory[^1];

        var hasParent = (await _runner.RunAsync(
            "rev-parse", "--verify", "--quiet", $"{oldest}^").ConfigureAwait(false)).Success;
        var chainResult = hasParent
            ? await _runner.RunAsync("rev-list", "--reverse", $"{oldest}^..{newest}").ConfigureAwait(false)
            : await _runner.RunAsync("rev-list", "--reverse", newest).ConfigureAwait(false);
        if (!chainResult.Success) return chainResult;
        var chain = SplitLines(chainResult.Output);
        if (chain.Count != selectedHistory.Count || !chain.ToHashSet().SetEquals(selectedHistory))
            return new GitCommandResult(-1, "", "連続したコミットを選択してください（範囲の途中に選択していないコミットやマージがあります）。");

        var rewriteRange = hasParent ? $"{oldest}^..HEAD" : "HEAD";
        var merges = await _runner.RunAsync("rev-list", "--min-parents=2", rewriteRange)
            .ConfigureAwait(false);
        if (!merges.Success) return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "", "選択範囲から HEAD までにマージコミットがあるため、スカッシュできません。");

        var aboveResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", $"{newest}..HEAD").ConfigureAwait(false);
        if (!aboveResult.Success) return aboveResult;
        var above = SplitLines(aboveResult.Output);

        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var messagePath = Path.Combine(gitDirectory, "loomo-squash-message.txt");
        var todo = new StringBuilder().Append("pick ").Append(chain[0]).Append('\n');
        for (var index = 1; index < chain.Count; index++)
            todo.Append(commitMessage is null ? "squash " : "fixup ").Append(chain[index]).Append('\n');
        if (commitMessage is not null)
            todo.Append("exec git commit --amend -F '").Append(ToMsysPath(messagePath)).Append("'\n");
        foreach (var commit in above)
            todo.Append("pick ").Append(commit).Append('\n');

        var extraFiles = commitMessage is null
            ? null
            : new[] { ("loomo-squash-message.txt", commitMessage.TrimEnd() + Environment.NewLine) };
        return await RunScriptedRebaseAsync(
            "loomo-squash-todo.txt", todo.ToString(), hasParent ? $"{oldest}^" : "--root", extraFiles)
            .ConfigureAwait(false);
    }

    private async Task<GitCommandResult> RunScriptedRebaseAsync(
        string todoFileName,
        string todoText,
        string baseArgument,
        IReadOnlyList<(string FileName, string Content)>? extraFiles = null)
    {
        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");

        var todoPath = Path.Combine(gitDirectory, todoFileName);
        var extraPaths = (extraFiles ?? Array.Empty<(string FileName, string Content)>())
            .Select(file => (Path: Path.Combine(gitDirectory, file.FileName), file.Content)).ToList();
        var keepExtraFiles = false;
        try
        {
            await File.WriteAllTextAsync(todoPath, todoText).ConfigureAwait(false);
            foreach (var (path, content) in extraPaths)
                await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
            var environment = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp '{ToMsysPath(todoPath)}'",
            };
            var result = await _runner.RunAsync(
                environment, "rebase", "-i", baseArgument).ConfigureAwait(false);
            keepExtraFiles = !result.Success && IsRebaseInProgress(gitDirectory);
            return result;
        }
        finally
        {
            TryDelete(todoPath);
            if (!keepExtraFiles)
                foreach (var (path, _) in extraPaths)
                    TryDelete(path);
        }
    }

    private static bool IsRebaseInProgress(string gitDirectory) =>
        Directory.Exists(Path.Combine(gitDirectory, "rebase-merge"))
        || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply"));

    public async Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)>
        GetCandidatesAsync(string fromHash)
    {
        var onHead = await _runner.RunAsync(
            "merge-base", "--is-ancestor", fromHash, "HEAD").ConfigureAwait(false);
        if (!onHead.Success)
            return (Array.Empty<RebasePlanEntry>(), "現在のブランチに含まれるコミットのみ対象にできます。");

        var hasParent = (await _runner.RunAsync(
            "rev-parse", "--verify", "--quiet", $"{fromHash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{fromHash}^..HEAD" : "HEAD";
        var chainResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success)
            return (Array.Empty<RebasePlanEntry>(), chainResult.Message);
        var chain = SplitLines(chainResult.Output);
        if (chain.Count == 0 || !string.Equals(chain[0], fromHash, StringComparison.OrdinalIgnoreCase))
            return (Array.Empty<RebasePlanEntry>(), "現在のブランチの主系列にあるコミットのみ対象にできます。");

        var merges = await _runner.RunAsync("rev-list", "--min-parents=2", range)
            .ConfigureAwait(false);
        if (!merges.Success)
            return (Array.Empty<RebasePlanEntry>(), merges.Message);
        if (SplitLines(merges.Output).Count > 0)
            return (Array.Empty<RebasePlanEntry>(),
                "対象から HEAD までにマージコミットがあるため、インタラクティブリベースできません。");

        var detail = await _runner.RunAsync(
            "log", "--reverse", "--first-parent", "--pretty=format:%H%x1f%h%x1f%s", range)
            .ConfigureAwait(false);
        if (!detail.Success)
            return (Array.Empty<RebasePlanEntry>(), detail.Message);

        var entries = new List<RebasePlanEntry>();
        foreach (var line in detail.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\x1f');
            if (parts.Length >= 3)
                entries.Add(new RebasePlanEntry(
                    parts[0], parts[1], parts[2], RebaseAction.Pick));
        }
        return (entries, null);
    }

    public async Task<GitCommandResult> InteractiveRebaseAsync(
        string fromHash,
        IReadOnlyList<RebasePlanEntry> plan,
        IReadOnlyDictionary<string, string> newMessages)
    {
        try
        {
            return await InteractiveRebaseCoreAsync(fromHash, plan, newMessages).ConfigureAwait(false);
        }
        finally
        {
            _mutations.NotifyRepositoryChanged();
        }
    }

    private async Task<GitCommandResult> InteractiveRebaseCoreAsync(
        string fromHash,
        IReadOnlyList<RebasePlanEntry> plan,
        IReadOnlyDictionary<string, string> newMessages)
    {
        if (plan.Count == 0)
            return new GitCommandResult(-1, "", "リベース対象がありません。");
        var onHead = await _runner.RunAsync(
            "merge-base", "--is-ancestor", fromHash, "HEAD").ConfigureAwait(false);
        if (onHead.ExitCode != 0)
            return onHead.ExitCode == 1
                ? new GitCommandResult(-1, "", "現在のブランチに含まれるコミットのみ対象にできます。")
                : onHead;

        var hasParent = (await _runner.RunAsync(
            "rev-parse", "--verify", "--quiet", $"{fromHash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{fromHash}^..HEAD" : "HEAD";
        var chainResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success) return chainResult;
        var chain = SplitLines(chainResult.Output);
        var planHashes = plan.Select(entry => entry.Hash).ToList();
        if (chain.Count != planHashes.Count || !chain.ToHashSet().SetEquals(planHashes))
            return new GitCommandResult(-1, "", "対象のコミット構成が変わったため実行できません。一覧を開き直してください。");

        var merges = await _runner.RunAsync("rev-list", "--min-parents=2", range)
            .ConfigureAwait(false);
        if (!merges.Success) return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "", "対象から HEAD までにマージコミットがあるため、インタラクティブリベースできません。");

        var firstNonDrop = plan.FirstOrDefault(entry => entry.Action != RebaseAction.Drop);
        if (firstNonDrop is null)
            return new GitCommandResult(-1, "", "少なくとも1件は pick / reword / edit にしてください。");
        if (firstNonDrop.Action is RebaseAction.Squash or RebaseAction.Fixup)
            return new GitCommandResult(-1, "", "先頭のコミットは pick / reword / edit のいずれかにしてください。");
        foreach (var entry in plan)
            if (entry.Action == RebaseAction.Reword && !newMessages.ContainsKey(entry.Hash))
                return new GitCommandResult(-1, "", $"{entry.ShortHash} の新しいメッセージが入力されていません。");

        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var extraFiles = new List<(string FileName, string Content)>();
        var todo = new StringBuilder();
        foreach (var entry in plan)
        {
            if (entry.Action == RebaseAction.Reword)
            {
                var fileName = $"loomo-rebase-msg-{entry.Hash}.txt";
                todo.Append("pick ").Append(entry.Hash).Append('\n')
                    .Append("exec git commit --amend -F '")
                    .Append(ToMsysPath(Path.Combine(gitDirectory, fileName))).Append("'\n");
                extraFiles.Add((fileName, newMessages[entry.Hash].TrimEnd() + Environment.NewLine));
            }
            else
            {
                var action = entry.Action switch
                {
                    RebaseAction.Drop => "drop",
                    RebaseAction.Squash => "squash",
                    RebaseAction.Fixup => "fixup",
                    RebaseAction.Edit => "edit",
                    _ => "pick",
                };
                todo.Append(action).Append(' ').Append(entry.Hash).Append('\n');
            }
        }

        return await RunScriptedRebaseAsync(
            "loomo-rebase-todo.txt", todo.ToString(), hasParent ? $"{fromHash}^" : "--root", extraFiles)
            .ConfigureAwait(false);
    }

    internal async Task DeleteScriptedArtifactsAsync()
    {
        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null) return;
        TryDelete(Path.Combine(gitDirectory, "loomo-squash-message.txt"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(gitDirectory, "loomo-rebase-msg-*.txt"))
                TryDelete(file);
        }
        catch { /* 列挙失敗は無視 */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 後始末の失敗は無視 */ }
    }

    private static List<string> SplitLines(string value) => value
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    private static string ToMsysPath(string path)
    {
        var value = path.Replace('\\', '/');
        if (value.Length >= 2 && value[1] == ':')
            value = "/" + char.ToLowerInvariant(value[0]) + value[2..];
        return value;
    }
}
