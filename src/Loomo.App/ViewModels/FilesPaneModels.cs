using CommunityToolkit.Mvvm.ComponentModel;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ファイル一覧ペインの1行。ツリー（<see cref="FileNodeViewModel"/>）と違い子を持たず、
/// 一覧・並べ替えのための素の値（サイズ・更新日時・種類）を持つ。アイコンはツリーと同じ
/// <see cref="FileIcons"/> から引く（種別ごとの共有インスタンスなので都度引いても配列参照ぶん）。</summary>
public sealed partial class FileEntryViewModel : ObservableObject
{
    private readonly int _iconIndex;

    public FileEntryViewModel(string fullPath, bool isDirectory, long size, DateTime modified, bool isHidden = false)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        Name = string.IsNullOrEmpty(name) ? fullPath : name;
        IsHidden = isHidden;
        _size = size;
        _modified = modified;
        _iconIndex = FileIcons.IndexFor(fullPath, isDirectory);
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    /// <summary>隠し属性（またはシステム属性）が付いているか。<c>.git</c> のような作業に無関係な
    /// フォルダーは既定で伏せる（「隠しファイルを表示」で戻る）。</summary>
    public bool IsHidden { get; }

    /// <summary>バイト数（フォルダーは 0）。監視更新で既存インスタンスを再利用するため可変。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedText))]
    private DateTime _modified;

    public ImageSource IconImage => IsDirectory
        ? FileIcons.FolderImage(open: false)
        : FileIcons.ImageFor(_iconIndex);

    /// <summary>テーマの明暗が変わってアイコンの配色が入れ替わったとき、引き直させる。</summary>
    public void RefreshIcon() => OnPropertyChanged(nameof(IconImage));

    public string SizeText => IsDirectory ? "" : FormatSize(Size);

    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy/MM/dd HH:mm");

    /// <summary>「種類」列。フォルダーは <c>フォルダー</c>、ファイルは拡張子（大文字・ドット無し）。
    /// 拡張子の無いファイルは空欄にせず <c>ファイル</c> と書く（空欄は読み手には欠測に見える）。</summary>
    public string TypeText => IsDirectory
        ? "フォルダー"
        : Path.GetExtension(Name) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() : "ファイル";

    /// <summary>種類での並べ替えキー（表示と違い、比較が安定するよう小文字のまま）。</summary>
    public string TypeKey => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    public bool IsHtml => !IsDirectory
        && (FullPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || FullPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double value = bytes;
        foreach (var unit in new[] { "KB", "MB", "GB", "TB" })
        {
            value /= 1024;
            if (value < 1024)
                return value < 10 ? $"{value:0.0} {unit}" : $"{value:0} {unit}";
        }
        return $"{value:0} TB";
    }
}

/// <summary>パンくずの1区切り。クリックすると、その階層のフォルダー候補を開く。</summary>
public sealed record FilesBreadcrumb(string Name, string FullPath, bool IsLast);

/// <summary>「場所」候補の出どころ。表示の並び順もこの順（近いものから遠いものへ）。</summary>
public enum FilesPlaceKind
{
    /// <summary>ワークスペースフォルダー（プライマリ＋追加）。</summary>
    WorkspaceFolder,
    /// <summary>ピン留め。ツリーと共有する（<see cref="IFolderPinStore"/>）。</summary>
    Pinned,
    /// <summary>Windows エクスプローラーのクイックアクセス。</summary>
    QuickAccess,
    /// <summary>ドライブのルート。</summary>
    Drive
}

/// <summary>「場所」ポップアップの1項目。</summary>
public sealed record FilesPlace(string Name, string FullPath, FilesPlaceKind Kind);

/// <summary>「場所」ポップアップの1グループ（見出し＋項目）。</summary>
public sealed record FilesPlaceGroup(string Name, IReadOnlyList<FilesPlace> Items);

/// <summary>一覧の並べ替え・絞り込み（純関数）。ペインの表示計算はここに閉じるので、
/// WPF 無しでそのまま検証できる。</summary>
public static class FilesListing
{
    /// <summary>絞り込み → 並べ替えを適用した表示順を返す。フォルダーは常に先（ファイル管理の作法で、
    /// 並べ替え列を変えても入れ替わらない）。</summary>
    public static List<FileEntryViewModel> Arrange(
        IEnumerable<FileEntryViewModel> source,
        FilesSortColumn column,
        bool descending,
        string filter,
        bool showHidden)
    {
        var matches = MatcherFor(filter);
        var items = source
            .Where(e => (showHidden || !e.IsHidden) && matches(e.Name))
            .ToList();

        items.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;   // フォルダーが先（昇順・降順に関わらず）
            var c = column switch
            {
                FilesSortColumn.Size => a.Size.CompareTo(b.Size),
                FilesSortColumn.Modified => a.Modified.CompareTo(b.Modified),
                FilesSortColumn.Type => string.CompareOrdinal(a.TypeKey, b.TypeKey),
                _ => CompareNatural(a.Name, b.Name),
            };
            if (c != 0)
                return descending ? -c : c;
            // 同値のときは常に名前昇順で決める（更新のたびに並びが揺れないように）。
            return CompareNatural(a.Name, b.Name);
        });
        return items;
    }

    /// <summary>絞り込みの判定。<c>*</c>／<c>?</c> を含めばワイルドカード（全体一致）、
    /// 含まなければ部分一致。どちらも大文字小文字を区別しない。</summary>
    public static Func<string, bool> MatcherFor(string filter)
    {
        filter = filter?.Trim() ?? "";
        if (filter.Length == 0)
            return static _ => true;

        if (filter.Contains('*') || filter.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return name => regex.IsMatch(name);
        }

        var needle = filter;
        return name => name.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>数字を数として読む名前比較（file2 が file10 より前に来る）。
    /// 単純な序数比較だと "file10" &lt; "file2" になり、連番のファイルが並ばない。</summary>
    public static int CompareNatural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length)
                    return na.Length - nb.Length;      // 桁数がそのまま大小
                var digits = na.SequenceCompareTo(nb); // 同桁なら文字列比較＝数値比較
                if (digits != 0)
                    return digits;
                continue;
            }

            var ca = char.ToUpperInvariant(a[i]);
            var cb = char.ToUpperInvariant(b[j]);
            if (ca != cb)
                return ca - cb;
            i++;
            j++;
        }
        return (a.Length - i) - (b.Length - j);
    }
}
