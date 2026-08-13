using System.Collections.Specialized;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイルのクリップボード授受（Windows のファイルドロップリスト形式なので
/// エクスプローラーと相互運用できる）。切り取りは「Preferred DropEffect」に Move を入れて区別し、
/// 貼り付けに成功したら一度だけ消える。
///
/// <para>サイドバーのツリー（<see cref="Views.FolderTreeView"/>）とファイル一覧ペイン
/// （<see cref="Views.FilesPaneView"/>）の共有物。ここを分けると「切り取りが片方だけ移動にならない」
/// 類の食い違いが必ず出る——同じ素材を同じ規則で受け渡すための1箇所。</para></summary>
public static class FileClipboard
{
    /// <summary>パスをファイルドロップリストとして載せる。<paramref name="move"/> が true なら切り取り。</summary>
    public static void SetFiles(IEnumerable<string> paths, bool move)
    {
        var list = new StringCollection();
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path))
                list.Add(path);
        if (list.Count == 0)
            return;

        try
        {
            var data = new DataObject();
            data.SetFileDropList(list);
            // Preferred DropEffect: Copy=5 / Move=2。切り取りだけ Move を入れる。
            var effect = move ? DragDropEffects.Move : DragDropEffects.Copy;
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)effect)));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch { /* クリップボードのロック等は無視 */ }
    }

    public static bool ContainsFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }

    public static IReadOnlyList<string> GetFiles()
    {
        try
        {
            return Clipboard.GetFileDropList().Cast<string?>()
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>クリップボードが「切り取り」（移動希望）か。</summary>
    public static bool PrefersMove()
    {
        try
        {
            if (Clipboard.GetDataObject()?.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var bytes = new byte[4];
                _ = ms.Read(bytes, 0, 4);
                var effect = (DragDropEffects)BitConverter.ToInt32(bytes, 0);
                return (effect & DragDropEffects.Move) != 0 && (effect & DragDropEffects.Copy) == 0;
            }
        }
        catch { /* 無視 */ }
        return false;
    }

    public static void Clear()
    {
        try { Clipboard.Clear(); }
        catch { /* 無視 */ }
    }

    /// <summary>複数のパス・名前を1行1件で載せる（行区切りならどこへ貼っても壊れない）。</summary>
    public static void CopyLines(IEnumerable<string> values)
    {
        var text = string.Join(Environment.NewLine, values);
        if (text.Length == 0)
            return;
        try { Clipboard.SetText(text); }
        catch { /* クリップボードのロック等は無視 */ }
    }
}
