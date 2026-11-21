namespace sk0ya.Loomo.App.Services;

/// <summary>エディタで見ているファイルに、エクスプローラの選択を合わせる先を決める。
///
/// <para>辿るのは<b>いま開いている枝だけ</b>。畳まれたフォルダーは開かない——ツリーは既定で畳んでおき、
/// 開くのは人の操作だけ、という方針を崩さないため。見えている行に無ければ「選択を外す」を返す：
/// 別のファイルの行が選ばれたまま残ると、ツリーがいま見ているファイルを偽って示してしまう。</para></summary>
internal static class FolderTreeActiveFileFollow
{
    /// <summary>選ぶ行。<c>null</c> は「見えている行に無い（選択を外す）」。</summary>
    internal static T? VisibleRowFor<T>(
        IEnumerable<T> roots,
        string? fullPath,
        Func<T, string> getPath,
        Func<T, bool> isDirectory,
        Func<T, bool> isExpanded,
        Func<T, IEnumerable<T>> children)
        where T : class
    {
        if (string.IsNullOrEmpty(fullPath))
            return null;

        var level = roots;
        while (true)
        {
            var (target, descend) = FilePathRelations.FindPathMatch(level, fullPath, getPath, isDirectory);
            if (target is not null)
                return target;

            // まとめ表示の子ファイルは、フォルダーではなく親ファイルの下に居る。
            descend ??= level.FirstOrDefault(node => !isDirectory(node)
                && children(node).Any(child => FilePathRelations.AreEqual(getPath(child), fullPath)));
            if (descend is null || !isExpanded(descend))
                return null;

            level = children(descend);
        }
    }
}
