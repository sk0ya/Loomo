using System.IO;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>ほかのワークツリーにある「同じファイル」（§24.80）。<see cref="Exists"/> が false ならその版にはまだ無い（または消した）。</summary>
public sealed record WorktreeCounterpart(string WorktreePath, string Label, string FilePath, bool Exists);

/// <summary>相手のファイルをどうするか（§24.80）。</summary>
public enum WorktreeCounterpartAction
{
    /// <summary>この部屋のエディタで開く（同じ行へ）。</summary>
    OpenHere,
    /// <summary>このファイルと差分で比べる（左＝相手、右＝いま開いているもの）。</summary>
    Compare,
    /// <summary>その部屋へ移ってから開く（§24.17.1 の切替）。</summary>
    OpenInRoom,
}

/// <summary>TABS から ShellWindow へ渡す要求（§24.80）。</summary>
public sealed record WorktreeCounterpartRequest(
    sk0ya.Loomo.App.ViewModels.TabEntryViewModel Tab, WorktreeCounterpart Target, WorktreeCounterpartAction Action);

/// <summary>
/// いま開いているファイルの、ほかのワークツリーでの同じ相対パス（§24.80・Emacs 31.1 の <c>vc-switch-working-tree</c>）。
/// ワークツリーどうしは相対パスの構成が同じなので、持ち主のワークツリー（それを含むいちばん深いもの）からの相対パスを
/// 相手のワークツリーに付け直すだけで引ける（部屋の写し §24.17.1 の <c>WorktreeRoomSeed.PathMap</c> と同じ考え方）。
/// </summary>
internal static class WorktreeCounterparts
{
    /// <param name="worktrees">（パス, ブランチ名。デタッチは null）。並びはそのまま一覧の順になる。</param>
    public static IReadOnlyList<WorktreeCounterpart> For(
        string filePath, IEnumerable<(string Path, string? Branch)> worktrees, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
            return [];
        var list = worktrees.Select(w => (Path: Trim(Path.GetFullPath(w.Path)), w.Branch)).ToList();
        // 値のタプルなので、空のときに例外を投げる MaxBy ではなく並べて先頭を取る。
        var owners = list.Where(w => WorkspacePaths.IsWithin(w.Path, filePath)).OrderByDescending(w => w.Path.Length).ToList();
        if (owners.Count == 0)
            return [];
        var owner = owners[0];
        var relative = Path.GetRelativePath(owner.Path, filePath);
        return list
            .Where(w => !string.Equals(w.Path, owner.Path, StringComparison.OrdinalIgnoreCase))
            .Select(w =>
            {
                var path = Path.Combine(w.Path, relative);
                return new WorktreeCounterpart(w.Path, LabelOf(w.Path, w.Branch), path, exists(path));
            })
            .ToList();
    }

    private static string LabelOf(string path, string? branch)
    {
        var folder = WorkspacePaths.FolderName(path);
        return branch is { Length: > 0 } ? $"{branch}（{folder}）" : $"{folder}（ブランチなし）";
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
