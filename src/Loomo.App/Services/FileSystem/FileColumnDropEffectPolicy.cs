namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧カラムでのドロップ操作を決める。</summary>
internal enum FileColumnDropEffect
{
    None,
    Copy,
    Move,
}

internal static class FileColumnDropEffectPolicy
{
    public static FileColumnDropEffect Resolve(
        IReadOnlyList<string> sources,
        string targetDirectory,
        bool internalDrag,
        bool controlPressed,
        bool shiftPressed,
        Func<string, bool> isDirectory,
        bool preventSameDirectoryMove = true)
    {
        foreach (var source in sources)
        {
            // フォルダを自身／配下へは不可（無限再帰）。
            if (isDirectory(source)
                && FilePathRelations.IsSameOrAncestorOf(source, targetDirectory))
                return FileColumnDropEffect.None;

            // 同じフォルダーへの移動は何も起きないので受けない。
            if (preventSameDirectoryMove
                && internalDrag
                && FilePathRelations.AreEqual(Path.GetDirectoryName(source), targetDirectory)
                && !controlPressed)
                return FileColumnDropEffect.None;
        }

        if (controlPressed)
            return FileColumnDropEffect.Copy;
        if (shiftPressed)
            return FileColumnDropEffect.Move;
        return internalDrag ? FileColumnDropEffect.Move : FileColumnDropEffect.Copy;
    }
}
