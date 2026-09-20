namespace sk0ya.Loomo.App.Services;

/// <summary>ブックマークの入れ物（フォルダー）1つ。<see cref="BrowserBookmarkTree.Build"/> が
/// 一列のブックマークから組み立てる、表示用の木。</summary>
public sealed class BookmarkFolder
{
    /// <summary>このフォルダーの名前（根だけ空文字）。</summary>
    public required string Name { get; init; }

    /// <summary>根からここまでの道。表示の並びと展開状態の鍵に使う。</summary>
    public required IReadOnlyList<string> Path { get; init; }

    public List<BookmarkFolder> Folders { get; } = new();
    public List<BrowserBookmark> Bookmarks { get; } = new();

    /// <summary>この下（入れ子を含む）にあるブックマークの総数。畳んでいても件数だけは見せる。</summary>
    public int TotalCount { get; internal set; }
}

/// <summary>一覧に出す1行（フォルダーの行か、ブックマークの行のどちらか）。</summary>
public sealed record BookmarkRow(int Depth, BookmarkFolder? Folder, BrowserBookmark? Bookmark);

/// <summary>
/// ブックマークの階層に関する判断をまとめた純関数群（UI を触らないのでテストできる）。
///
/// <para>永続化の正本は <see cref="BrowserLibrarySnapshot.Bookmarks"/> の<b>一列</b>のままで、
/// 木はそこから毎回組み直す。入れ子の入れ物を JSON にそのまま書くと、フォルダーの追加・移動・
/// 削除のたびに構造を書き換えることになり、URL 単位で引く用途（★の判定・アドレス欄の候補）が
/// 木を歩く羽目になる——「置き場所」は1件が持つ属性（<see cref="BrowserBookmark.Folder"/>）で足りる。</para>
/// </summary>
public static class BrowserBookmarkTree
{
    /// <summary>展開状態を覚えるための鍵。フォルダー名に <c>/</c> が入っていても混ざらないよう、
    /// 表示に現れない文字で繋ぐ。</summary>
    public static string Key(IEnumerable<string> path) => string.Join(PathSeparator, path);

    /// <summary>道を1本の鍵に繋ぐ区切り。表示に現れない制御文字なので、フォルダー名に何が入っていても衝突しない。</summary>
    private const char PathSeparator = (char)0x1f;

    /// <summary>置き場所の正規化（前後の空白と空の段を落とす）。手で書かれた値も取り込んだ値も
    /// ここを通してから比べる。</summary>
    public static List<string> NormalizePath(IEnumerable<string>? folder)
        => folder is null
            ? new List<string>()
            : folder.Select(s => s?.Trim() ?? "").Where(s => s.Length > 0).ToList();

    /// <summary>一列のブックマークから木を組む。返るのは根（名前なし）で、
    /// 同じ段の中ではフォルダーは<b>最初に出てきた順</b>、ブックマークは元の並びのまま。</summary>
    public static BookmarkFolder Build(IEnumerable<BrowserBookmark> bookmarks)
    {
        var root = new BookmarkFolder { Name = "", Path = Array.Empty<string>() };
        foreach (var bookmark in bookmarks)
        {
            var folder = root;
            foreach (var segment in NormalizePath(bookmark.Folder))
                folder = Descend(folder, segment);
            folder.Bookmarks.Add(bookmark);
        }
        CountUp(root);
        return root;
    }

    private static BookmarkFolder Descend(BookmarkFolder parent, string name)
    {
        var found = parent.Folders.FirstOrDefault(
            f => string.Equals(f.Name, name, StringComparison.Ordinal));
        if (found is not null)
            return found;
        var child = new BookmarkFolder { Name = name, Path = parent.Path.Append(name).ToList() };
        parent.Folders.Add(child);
        return child;
    }

    private static int CountUp(BookmarkFolder folder)
        => folder.TotalCount = folder.Bookmarks.Count + folder.Folders.Sum(CountUp);

    /// <summary>木を一覧の行に均す。開いているフォルダー（<paramref name="expanded"/> に鍵があるもの）の
    /// 中身だけを続けて出す——畳んだフォルダーの中は行を作らない。
    /// 同じ段ではフォルダーが先、ぶら下がっているブックマークが後（取り込み元のブラウザと同じ見え方）。</summary>
    public static List<BookmarkRow> Flatten(BookmarkFolder root, ISet<string> expanded)
    {
        var rows = new List<BookmarkRow>();
        Walk(root, 0);
        return rows;

        void Walk(BookmarkFolder folder, int depth)
        {
            foreach (var child in folder.Folders)
            {
                rows.Add(new BookmarkRow(depth, child, null));
                if (expanded.Contains(Key(child.Path)))
                    Walk(child, depth + 1);
            }
            foreach (var bookmark in folder.Bookmarks)
                rows.Add(new BookmarkRow(depth, null, bookmark));
        }
    }

    /// <summary>フォルダー（と、その中の入れ子）に入っているブックマークを列挙する。
    /// フォルダーごと消すときの対象。</summary>
    public static IEnumerable<BrowserBookmark> Descendants(BookmarkFolder folder)
        => folder.Bookmarks.Concat(folder.Folders.SelectMany(Descendants));

    /// <summary>フォルダーを深さ優先で列挙する。表示状態の一括操作に使う。</summary>
    public static IEnumerable<BookmarkFolder> Folders(BookmarkFolder root)
        => root.Folders.SelectMany(folder => new[] { folder }.Concat(Folders(folder)));
}
