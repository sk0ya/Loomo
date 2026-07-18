namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ワークスペース配下のファイル名検索・全文検索。</summary>
public interface IWorkspaceSearchService
{
    Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string query, int max, CancellationToken ct, string? searchRoot = null);
    Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null);
}

/// <summary>ファイル名検索の1ヒット。Score は小さいほど一致が強い。</summary>
public sealed record FileSearchHit(string FullPath, string RelativePath, int Score);
/// <summary>grep の1ヒット。Line と Column は1始まり。</summary>
public sealed record ContentSearchHit(string FullPath, string RelativePath, int Line, int Column, string LineText);
/// <summary>grep のオプション。</summary>
public sealed record GrepOptions(bool CaseSensitive = false, bool UseRegex = false, string? IncludeGlob = null, string? ExcludeGlob = null, int MaxResults = 500);
