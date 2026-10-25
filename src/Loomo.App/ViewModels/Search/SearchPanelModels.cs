using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

public enum SearchScope { Text, FileName, Advanced, Terminal, Class, Symbol }

public readonly record struct SearchKindOption(SearchFileKind Value, string Label);

/// <summary>検索フォルダー欄 ▾ の1候補。<see cref="Value"/> は <c>SearchRoot</c> にそのまま入る表記
/// （空＝ワークスペース全体）、<see cref="Detail"/> はメニューの右に添える同じ表記（全体のときは空）。</summary>
public sealed record SearchRootChoice(string Label, string Detail, string Value, bool IsPin, bool IsCurrent);

/// <summary>ターミナル検索の1件。<see cref="TabId"/>／<see cref="TabTitle"/> はどのタブの一致か（§24.67 で全部のタブを
/// 探すようになったので、行番号だけでは戻り先が決まらない）。</summary>
public readonly record struct TerminalSearchHit(
    int LineIndex, int Column, int Length, string LineText, Guid TabId = default, string? TabTitle = null);

/// <summary>ターミナル検索の一致の決まりごと（§24.67）。</summary>
public static class TerminalSearchHits
{
    /// <summary>まだ表示していないタブ（常駐ホストに探してもらった）の一致の行番号。端末の行番号と揃わないので、戻るときに探し直す印。</summary>
    public const int UnseenLine = -1;

    /// <summary>戻るときに探し直す目印：行の頭（前後の空白を除いて 40 字）。空なら null。</summary>
    public static string? Anchor(string lineText)
    {
        var trimmed = lineText.Trim();
        return trimmed.Length == 0 ? null : trimmed.Length <= 40 ? trimmed : trimmed[..40];
    }
}

/// <summary>クラス／シンボル検索の結果。<see cref="HasConnection"/> が false なら
/// 「言語サーバーが未接続」（開いているコードタブが無い／未接続）として扱う。</summary>
public readonly record struct SymbolSearchResult(bool HasConnection, IReadOnlyList<LspSymbolInformation> Symbols)
{
    public static readonly SymbolSearchResult NoConnection = new(false, Array.Empty<LspSymbolInformation>());
}

public sealed partial class SearchMatchItem : ObservableObject
{
    public SearchMatchItem(ContentSearchHit hit)
    {
        FullPath = hit.FullPath;
        RelativePath = hit.RelativePath;
        Line = hit.Line;
        Column = hit.Column;
        LineText = hit.LineText.TrimEnd();
        Location = hit.Location;
    }

    private SearchMatchItem(TerminalSearchHit hit)
    {
        IsTerminal = true;
        TerminalHit = hit;
        FullPath = "";
        RelativePath = "";
        Line = hit.LineIndex + 1;
        Column = hit.Column;
        LineText = hit.LineText.TrimEnd();
    }

    private SearchMatchItem(string fullPath, string relativePath, int line, int column, string preview)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Line = line;
        Column = column;
        LineText = preview;
    }

    public static SearchMatchItem ForTerminal(TerminalSearchHit hit) => new(hit);

    /// <summary>クラス／シンボル検索（LSP のワークスペースシンボル）の1件。</summary>
    public static SearchMatchItem ForSymbol(string fullPath, string relativePath, int line, int column, string preview)
        => new(fullPath, relativePath, line, column, preview);

    /// <summary>タブに残した検索結果（<see cref="SearchResultTab"/>）の1件。残した時点の行テキストをそのまま出す。</summary>
    public static SearchMatchItem ForSnapshot(string fullPath, string relativePath, int line, int column, string lineText,
        string? location = null)
        => new(fullPath, relativePath, line, column, lineText.TrimEnd()) { Location = location };
    public bool IsTerminal { get; }
    public TerminalSearchHit TerminalHit { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public int Line { get; }
    public int Column { get; }
    public string LineText { get; }
    public string Preview => LineText.TrimStart();

    /// <summary>文書（PDF・Word・Excel・PowerPoint）の本文の一致か（<see cref="Line"/> は行ではなく、開いたときに
    /// 移る先の番号＝ページ・シート・スライド・段落）。</summary>
    public bool IsDocumentHit => !IsTerminal && SearchDocuments.IsDocument(FullPath);

    /// <summary>文書の一致の位置の表記（"p.3"・"Sheet1!B3"・"スライド 4"・"¶12"）。テキストファイルは null。</summary>
    public string? Location { get; private init; }

    /// <summary>一致行の左に出す位置。文書は位置の表記（古いタブの写しで表記が無い PDF はページ）、それ以外は行番号。</summary>
    public string LineLabel => Location
        ?? (IsDocumentHit && SearchDocuments.IsPdf(FullPath) ? $"p.{Line}" : Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public bool IsExpanded { get; set; }

    /// <summary>この場でもう置換した（1件だけ置換・置換して次へ）。一覧からは消さず取り消し線で示す
    /// （置換のたびに一覧が消えたり並び直ったりすると、次々処理していく操作の邪魔になるため）。</summary>
    [ObservableProperty] private bool _isReplaced;
}

public sealed partial class SearchFileGroup : ObservableObject
{
    public SearchFileGroup(string fullPath, string relativePath, IEnumerable<SearchMatchItem> matches)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Matches = new ObservableCollection<SearchMatchItem>(matches);
        _iconIndex = FileIcons.IndexFor(fullPath, isDirectory: false);
    }

    public SearchFileGroup(AdvancedFileSearchHit hit)
        : this(hit.FullPath, hit.RelativePath, hit.ContentMatches.Select(h => new SearchMatchItem(h)))
    {
        Size = hit.Size;
        LastWriteTimeUtc = hit.LastWriteTimeUtc;
        HasMetadata = true;
    }

    [ObservableProperty] private bool _isExpanded = true;
    public string FullPath { get; }
    public string RelativePath { get; }
    public ObservableCollection<SearchMatchItem> Matches { get; }
    private readonly int _iconIndex;
    public ImageSource IconImage => FileIcons.ImageFor(_iconIndex);
    public int Count => Matches.Count;
    public string FileName => Segment(afterSlash: true);
    public string FolderPath => Segment(afterSlash: false);
    public bool ShowCount => Count > 0;
    public bool HasMetadata { get; }
    public long Size { get; }
    public DateTime LastWriteTimeUtc { get; }
    public string MetadataText => HasMetadata
        ? $"{FormatSize(Size)}  {LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "";

    private static string FormatSize(long size)
        => size switch
        {
            < 1024 => $"{size} B",
            < 1024 * 1024 => $"{size / 1024d:0.#} KB",
            < 1024 * 1024 * 1024 => $"{size / (1024d * 1024):0.#} MB",
            _ => $"{size / (1024d * 1024 * 1024):0.#} GB",
        };

    private string Segment(bool afterSlash)
    {
        var index = RelativePath.LastIndexOf('/');
        if (index < 0) return afterSlash ? RelativePath : "";
        return afterSlash ? RelativePath[(index + 1)..] : RelativePath[..index];
    }
}

public sealed partial class SearchFolderNode : ObservableObject
{
    public SearchFolderNode(string name, string relativePath)
    {
        Name = name;
        RelativePath = relativePath;
    }

    // フォルダーアイコンは開閉で絵が変わるので、開閉に追随させる。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconImage))]
    private bool _isExpanded = true;

    public string Name { get; private set; }
    public string RelativePath { get; }
    public ObservableCollection<object> Children { get; } = new();
    public ImageSource IconImage => FileIcons.FolderImage(IsExpanded);
    public int Count => Children.Sum(child => child switch
    {
        SearchFolderNode folder => folder.Count,
        SearchFileGroup group => group.Count,
        _ => 0,
    });
    public bool ShowCount => Count > 0;

    internal void PrependName(string parentSegment)
    {
        if (!string.IsNullOrEmpty(parentSegment)) Name = parentSegment + "/" + Name;
    }
}

/// <summary>開く一致。文書（PDF・Excel 等）の一致は <paramref name="Line"/> が移る先の番号（ページ・シート等）で、
/// <paramref name="Location"/> に位置の表記（"Sheet1!B3" など）が入る。</summary>
public readonly record struct SearchHit(string FullPath, int Line, int Column, string Highlight = "", string? Location = null);

/// <summary>検索結果の文書（PDF・Word・Excel・PowerPoint）の扱い。文書の一致は Line が移る先の番号（ページ等）で、
/// 置換の対象にしない（本文はバイナリ・zip の中）。</summary>
public static class SearchDocuments
{
    public static bool IsDocument(string? path)
        => path is not null && sk0ya.Loomo.Services.Search.DocumentTextCache.IsDocument(path);

    public static bool IsPdf(string? path)
        => path is not null && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>文書の本文の一致をまとめる見出し（結果ツリーの末尾に置く）。</summary>
    public const string SectionName = "文書の本文";
}
