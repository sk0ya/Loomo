using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ファイル一覧の表示形式。数値で保存されるため末尾追加のみ可。</summary>
public enum FilesDisplayMode
{
    /// <summary>名前・サイズ・更新日時・種類を列で表示する。</summary>
    Details,
    /// <summary>小さいアイコンと名前を1行に表示する。</summary>
    List,
    /// <summary>大きいアイコンをグリッド表示する。</summary>
    LargeIcons,
    /// <summary>中くらいのアイコンをグリッド表示する。</summary>
    MediumIcons,
    /// <summary>小さいアイコンをグリッド表示する。</summary>
    SmallIcons,
    /// <summary>アイコンと属性を横に並べる。</summary>
    Tiles,
}

/// <summary>表示形式選択コンボボックスの項目。</summary>
public sealed record FilesDisplayModeOption(FilesDisplayMode Value, string Label);

public static class FilesDisplayModes
{
    public static IReadOnlyList<FilesDisplayModeOption> Options { get; } =
    [
        new(FilesDisplayMode.Details, "詳細"),
        new(FilesDisplayMode.List, "一覧"),
        new(FilesDisplayMode.LargeIcons, "大アイコン"),
        new(FilesDisplayMode.MediumIcons, "中アイコン"),
        new(FilesDisplayMode.SmallIcons, "小アイコン"),
        new(FilesDisplayMode.Tiles, "タイル"),
    ];

    /// <summary>保存値や外部からの設定値を、実際に選択できる表示形式へ丸める。</summary>
    public static FilesDisplayMode Normalize(FilesDisplayMode value)
        => Options.Any(option => option.Value == value) ? value : FilesDisplayMode.Details;
}

/// <summary>ファイル一覧のグループ化方法。数値で保存されるため末尾追加のみ可。</summary>
public enum FilesGroupBy
{
    None,
    Type,
    Modified,
    Size,
}

/// <summary>グループ化選択コンボボックスの項目。</summary>
public sealed record FilesGroupByOption(FilesGroupBy Value, string Label);

public static class FilesGrouping
{
    public static IReadOnlyList<FilesGroupByOption> Options { get; } =
    [
        new(FilesGroupBy.None, "グループ化なし"),
        new(FilesGroupBy.Type, "種類／拡張子"),
        new(FilesGroupBy.Modified, "更新日"),
        new(FilesGroupBy.Size, "サイズ"),
    ];

    public static FilesGroupBy Normalize(FilesGroupBy value)
        => Options.Any(option => option.Value == value) ? value : FilesGroupBy.None;
}

/// <summary>詳細表示で使う列。数値で保存されるため末尾追加のみ可。</summary>
public enum FilesColumnKey
{
    Name,
    Size,
    Modified,
    Type,
}

/// <summary>列設定の保存値。未知の列、重複、極端な幅は復元時に無視・正規化する。</summary>
public sealed class FilesColumnSettingSnapshot
{
    public FilesColumnKey Key { get; set; }
    public bool IsVisible { get; set; } = true;
    public double Width { get; set; }
}

/// <summary>フォルダーごとの詳細列レイアウト。</summary>
public sealed class FilesColumnLayoutSnapshot
{
    public List<FilesColumnSettingSnapshot> Columns { get; set; } = new();
}

/// <summary>列設定を表示・編集する行。</summary>
public sealed partial class FilesColumnSetting : ObservableObject
{
    public FilesColumnSetting(FilesColumnKey key, string label, double defaultWidth, bool canHide)
    {
        Key = key;
        Label = label;
        DefaultWidth = defaultWidth;
        CanHide = canHide;
        _width = defaultWidth;
    }

    public FilesColumnKey Key { get; }
    public string Label { get; }
    public double DefaultWidth { get; }
    public bool CanHide { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private double _width;
}

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

    /// <summary>Git作業ツリー上の状態。リポジトリ外・未読込・クリーンは None。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitStatusBadge))]
    [NotifyPropertyChangedFor(nameof(GitStatusTooltip))]
    private GitChangeKind _gitStatus;

    public string GitStatusBadge => GitStatus switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Untracked => "U",
        GitChangeKind.Conflicted => "C",
        GitChangeKind.Staged => "S",
        GitChangeKind.Ignored => "I",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.DirectoryChanged => "●",
        _ => "",
    };

    public string GitStatusTooltip => GitStatus switch
    {
        GitChangeKind.Modified => "変更",
        GitChangeKind.Untracked => "未追跡",
        GitChangeKind.Conflicted => "競合",
        GitChangeKind.Staged => "ステージ済み",
        GitChangeKind.Ignored => "無視対象",
        GitChangeKind.Added => "追加",
        GitChangeKind.Deleted => "削除",
        GitChangeKind.Renamed => "名前変更",
        GitChangeKind.DirectoryChanged => "配下に変更あり",
        _ => "クリーン／Git対象外",
    };

    /// <summary>隠し属性（またはシステム属性）が付いているか。<c>.git</c> のような作業に無関係な
    /// フォルダーは既定で伏せる（「隠しファイルを表示」で戻る）。</summary>
    public bool IsHidden { get; }

    /// <summary>バイト数（フォルダーは 0）。監視更新で既存インスタンスを再利用するため可変。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconImage))]
    private ImageSource? _thumbnailImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedText))]
    private DateTime _modified;

    /// <summary>サムネイルがまだ無い間も必ず通常アイコンを返す。これにより非同期取得中、
    /// 非対応形式、壊れたファイル、Shell 拡張の無い環境で表示が空白にならない。</summary>
    public ImageSource IconImage => ThumbnailImage ?? (IsDirectory
        ? FileIcons.FolderImage(open: false)
        : FileIcons.ImageFor(_iconIndex));

    /// <summary>テーマの明暗が変わってアイコンの配色が入れ替わったとき、引き直させる。</summary>
    public void RefreshIcon() => OnPropertyChanged(nameof(IconImage));

    public string SizeText => IsDirectory ? "" : FormatSize(Size);

    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy/MM/dd HH:mm");

    /// <summary>「種類」列。エクスプローラーと同じ種類名（<c>Markdown ソース ファイル</c> など）を
    /// シェルから引く。シェルが答えない環境では拡張子を大文字にしたものへ落ち、拡張子の無い
    /// ファイルは空欄にせず <c>ファイル</c> と書く（空欄は読み手には欠測に見える）。</summary>
    public string TypeText => ShellTypeNames.Describe(Name, IsDirectory);

    /// <summary>種類での並べ替えキー（表示と違い、比較が安定するよう小文字のまま）。</summary>
    public string TypeKey => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>現在のグループ化方法に応じた、表示名と並び順を持つグループ値。</summary>
    public FilesGroupValue GroupValue(FilesGroupBy groupBy) => FilesListing.GroupValue(this, groupBy);

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
    Drive,
    /// <summary>最近使ったファイル。</summary>
    RecentFile,
    /// <summary>頻繁に使うフォルダー。</summary>
    FrequentFolder
}

/// <summary>「場所」ポップアップの1項目。</summary>
public sealed record FilesPlace(string Name, string FullPath, FilesPlaceKind Kind)
{
    /// <summary>行頭のアイコン。ツリー・一覧と同じ <see cref="FileIcons"/> から引くので、
    /// 同じフォルダーが場所一覧とツリーで違う絵になることがない。名前だけが縦に並ぶ一覧は、
    /// フォルダーとファイルの区別が付かず走査しづらい。</summary>
    public ImageSource IconImage => Kind == FilesPlaceKind.RecentFile
        ? FileIcons.ImageFor(FileIcons.IndexFor(FullPath, isDirectory: false))
        : FileIcons.FolderImage(open: false);

    /// <summary>名前だけでは区別できない同名フォルダーのための補足（親フォルダー名）。
    /// ワークスペース・ピン留めは名前自体が場所を表すので付けない。</summary>
    public string Detail => Kind is FilesPlaceKind.QuickAccess or FilesPlaceKind.RecentFile
        or FilesPlaceKind.FrequentFolder
        ? Path.GetFileName(Path.GetDirectoryName(FullPath.TrimEnd('\\', '/')) ?? "")
        : "";
}

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
        bool showHidden,
        FilesGroupBy groupBy = FilesGroupBy.None)
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
        if (groupBy == FilesGroupBy.None)
            return items;

        // グループの順序は現在の並べ替え方向に合わせ、グループ内は従来の列ソートを保つ。
        var groups = items.GroupBy(entry => GroupValue(entry, groupBy));
        var orderedGroups = descending
            ? groups.OrderByDescending(group => group.Key.Order)
                .ThenByDescending(group => group.Key.Label, StringComparer.CurrentCultureIgnoreCase)
            : groups.OrderBy(group => group.Key.Order)
                .ThenBy(group => group.Key.Label, StringComparer.CurrentCultureIgnoreCase);
        return orderedGroups.SelectMany(group => group).ToList();
    }

    public static FilesGroupValue GroupValue(FileEntryViewModel entry, FilesGroupBy groupBy)
        => groupBy switch
        {
            FilesGroupBy.Type => entry.IsDirectory
                ? new FilesGroupValue("folder", "フォルダー", 0)
                : new FilesGroupValue(entry.TypeKey, entry.TypeText, 1),
            FilesGroupBy.Modified => entry.Modified == default
                ? new FilesGroupValue("unknown", "更新日時なし", int.MaxValue)
                : new FilesGroupValue(entry.Modified.Date.ToString("yyyyMMdd"),
                    entry.Modified.ToString("yyyy/MM/dd"), entry.Modified.Date.Ticks),
            FilesGroupBy.Size => entry.IsDirectory
                ? new FilesGroupValue("folder", "フォルダー", 0)
                : SizeGroup(entry.Size),
            _ => new FilesGroupValue("", "", 0),
        };

    private static FilesGroupValue SizeGroup(long size)
    {
        var (key, label, order) = size switch
        {
            0 => ("0", "0 B", 1),
            < 1024 => ("small", "1 B ～ 1 KB", 2),
            < 1024 * 1024 => ("kb", "1 KB ～ 1 MB", 3),
            < 1024L * 1024 * 1024 => ("mb", "1 MB ～ 1 GB", 4),
            _ => ("gb", "1 GB 以上", 5),
        };
        return new FilesGroupValue(key, label, order);
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

/// <summary>グループの識別子・表示名・並べ替え順。空の一覧では生成されないため、空グループが残らない。</summary>
public sealed record FilesGroupValue(string Key, string Label, long Order);
