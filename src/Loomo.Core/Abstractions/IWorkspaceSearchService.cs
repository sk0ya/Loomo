namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ワークスペース配下のファイル名検索・全文検索。</summary>
public interface IWorkspaceSearchService
{
    Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string query, int max, CancellationToken ct, string? searchRoot = null);
    Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null);
    Task<IReadOnlyList<AdvancedFileSearchHit>> SearchFilesAsync(AdvancedSearchOptions options, CancellationToken ct, string? searchRoot = null);
}

/// <summary>ファイル名検索の1ヒット。Score は小さいほど一致が強い。</summary>
public sealed record FileSearchHit(string FullPath, string RelativePath, int Score);
/// <summary>grep の1ヒット。Line と Column は1始まり。</summary>
public sealed record ContentSearchHit(string FullPath, string RelativePath, int Line, int Column, string LineText);
/// <summary>grep のオプション。</summary>
public sealed record GrepOptions(bool CaseSensitive = false, bool UseRegex = false, string? IncludeGlob = null, string? ExcludeGlob = null, int MaxResults = 500);

/// <summary>詳細検索のファイル種別。再解析点はサービス側で辿らない。</summary>
public enum SearchFileKind
{
    Any,
    Text,
    Code,
    Image,
    Video,
    Audio,
    Pdf,
    Archive,
}

/// <summary>ファイル名・内容・種別・サイズ・更新日時を組み合わせた再帰検索の条件。</summary>
public sealed record AdvancedSearchOptions(
    string? FileNameQuery = null,
    string? ContentQuery = null,
    bool CaseSensitive = false,
    bool UseRegex = false,
    string? ExtensionGlob = null,
    SearchFileKind Kind = SearchFileKind.Any,
    long? MinimumSize = null,
    long? MaximumSize = null,
    DateTime? ModifiedFrom = null,
    DateTime? ModifiedTo = null,
    int MaxResults = 500);

/// <summary>詳細検索の1ファイル。内容条件を指定した場合は一致行も含む。</summary>
public sealed record AdvancedFileSearchHit(
    string FullPath,
    string RelativePath,
    long Size,
    DateTime LastWriteTimeUtc,
    IReadOnlyList<ContentSearchHit> ContentMatches);
