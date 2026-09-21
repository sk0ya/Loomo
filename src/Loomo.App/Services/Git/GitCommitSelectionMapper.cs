using System.Collections;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>一覧の複数選択から、コミット行だけを操作用の並びへ変換する。</summary>
internal static class GitCommitSelectionMapper
{
    internal static GitLogRow? Commit(object? selectedItem)
        => selectedItem is GitLogRow { IsCommit: true } row ? row : null;

    internal static IReadOnlyList<GitLogRow> Commits(IEnumerable selectedItems)
        => selectedItems.OfType<GitLogRow>().Where(row => row.IsCommit).ToList();

    internal static string? Summary(GitLogRow? commit)
        => commit is null ? null : $"{commit.ShortHash} {commit.Subject}".Trim();
}
