using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークツリー作成ダイアログが知っているブランチの状況。</summary>
/// <param name="Local">ローカルブランチ。</param>
/// <param name="Remote">リモート追跡ブランチ（<c>origin/feature</c> の形）。</param>
/// <param name="CheckedOut">既にどこかのワークツリーでチェックアウトされているローカルブランチ
/// （git は同じブランチを2か所に置けない）。</param>
public sealed record GitWorktreeBranchContext(
    IReadOnlyCollection<string> Local, IReadOnlyCollection<string> Remote, IReadOnlyCollection<string> CheckedOut);

/// <summary>
/// ワークツリー作成ダイアログの入力検証と要求の組み立て（純ロジック）。git に投げて英語の fatal を
/// 見せる前に、分かっている失敗はここで日本語の理由にして止める。
/// </summary>
public static class GitWorktreeCreatePolicy
{
    /// <summary>入力の問題（無ければ null）。</summary>
    /// <param name="baseFolder">相対パスの基準（メインのワークツリー）。null ならプロセスの現在フォルダー。</param>
    public static string? Validate(
        GitWorktreeAddMode mode, string? branch, string? startPoint, string? path, GitWorktreeBranchContext branches,
        string? baseFolder = null)
    {
        var name = branch?.Trim();
        switch (mode)
        {
            case GitWorktreeAddMode.NewBranch:
                if (string.IsNullOrEmpty(name)) return "新しいブランチ名を入力してください。";
                if (!GitWorktreeArgs.IsValidReference(name)) return $"ブランチ名「{name}」は使えません（空白や先頭の - は不可）。";
                if (branches.Local.Contains(name)) return $"ブランチ「{name}」は既にあります。「既存のブランチ」で選んでください。";
                if (!string.IsNullOrWhiteSpace(startPoint) && !GitWorktreeArgs.IsValidReference(startPoint.Trim()))
                    return $"起点「{startPoint}」は使えません。";
                break;
            case GitWorktreeAddMode.ExistingBranch:
                if (string.IsNullOrEmpty(name)) return "置くブランチを選んでください。";
                if (!GitWorktreeArgs.IsValidReference(name)) return $"ブランチ名「{name}」は使えません。";
                var local = LocalBranchFor(name, branches);
                if (local is null) return $"ブランチ「{name}」が見つかりません。";
                if (branches.CheckedOut.Contains(local))
                    return $"ブランチ「{local}」は別のワークツリーでチェックアウト中です（同じブランチは2か所に置けません）。";
                break;
            case GitWorktreeAddMode.Detached:
                if (!string.IsNullOrWhiteSpace(startPoint) && !GitWorktreeArgs.IsValidReference(startPoint.Trim()))
                    return $"コミット「{startPoint}」は使えません。";
                break;
        }

        if (string.IsNullOrWhiteSpace(path)) return "置き場所を入力してください。";
        string full;
        try
        {
            full = ResolvePath(path, baseFolder);
        }
        catch (Exception)
        {
            return $"置き場所「{path}」はパスとして読めません。";
        }
        if (File.Exists(full)) return "置き場所に同名のファイルがあります。";
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            return "置き場所のフォルダーが空ではありません。別の場所を選んでください。";
        return null;
    }

    /// <summary>
    /// 検証を通った入力から git への要求を作る。<b>リモートのブランチを「既存のブランチ」で選んだら</b>
    /// 同名のローカルブランチを追跡付きで作る要求にする——<c>worktree add &lt;path&gt; origin/feature</c> と
    /// そのまま渡すと、git はそれを「コミット」として読んでデタッチで置いてしまう（ブランチにならない）。
    /// 同名のローカルが既にあれば、そちらを置く。
    /// </summary>
    public static GitWorktreeAddRequest BuildRequest(
        GitWorktreeAddMode mode, string? branch, string? startPoint, string path, GitWorktreeBranchContext branches,
        string? baseFolder = null)
    {
        var name = branch?.Trim();
        var start = string.IsNullOrWhiteSpace(startPoint) ? null : startPoint.Trim();
        var full = ResolvePath(path, baseFolder);
        switch (mode)
        {
            case GitWorktreeAddMode.NewBranch:
                return new GitWorktreeAddRequest(mode, full, name, start);
            case GitWorktreeAddMode.ExistingBranch when name is not null
                && !branches.Local.Contains(name) && branches.Remote.Contains(name):
                var local = LocalNameForRemote(name);
                return branches.Local.Contains(local)
                    ? new GitWorktreeAddRequest(GitWorktreeAddMode.ExistingBranch, full, local, null)
                    : new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, full, local, name);
            case GitWorktreeAddMode.ExistingBranch:
                return new GitWorktreeAddRequest(mode, full, name, null);
            default:
                return new GitWorktreeAddRequest(GitWorktreeAddMode.Detached, full, null, start);
        }
    }

    /// <summary>
    /// 置き場所の入力を絶対パスにする。相対パス（<c>..\app-feature</c>）は<b>リポジトリ</b>を基準にする——
    /// 素の <c>GetFullPath</c> は Loomo のプロセスの現在フォルダー基準で、空か確かめる先も作られる先も
    /// 利用者が思っている場所とずれる。
    /// </summary>
    public static string ResolvePath(string path, string? baseFolder)
    {
        var trimmed = path.Trim();
        return string.IsNullOrEmpty(baseFolder) || Path.IsPathFullyQualified(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(trimmed, baseFolder);
    }

    /// <summary><c>origin/feature/x</c> → <c>feature/x</c>（先頭のリモート名だけ落とす）。</summary>
    public static string LocalNameForRemote(string remoteBranch)
    {
        var slash = remoteBranch.IndexOf('/');
        return slash < 0 ? remoteBranch : remoteBranch[(slash + 1)..];
    }

    /// <summary>
    /// ブランチ名の入力に合わせて置き場所を自動で書き換えてよいか。利用者が置き場所を自分で打ち直したら
    /// （＝最後に自動で入れた値と違ったら）以後は触らない——打った場所をブランチ名の変更で上書きしない。
    /// </summary>
    public static bool ShouldFollowBranch(string? currentPath, string? lastSuggested)
        => string.IsNullOrWhiteSpace(currentPath)
           || string.Equals(currentPath, lastSuggested, StringComparison.OrdinalIgnoreCase);

    /// <summary>「既存のブランチ」の入力が最終的に置くローカルブランチ名（見つからなければ null）。</summary>
    private static string? LocalBranchFor(string name, GitWorktreeBranchContext branches)
        => branches.Local.Contains(name) ? name
            : branches.Remote.Contains(name) ? LocalNameForRemote(name)
            : null;
}
