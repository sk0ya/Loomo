namespace sk0ya.Loomo.App.Services;

/// <summary>このブランチで入れたコミット（§24.76）の印。一覧のセルは <c>DictionaryLookup</c> でハッシュから引く。</summary>
public static class GitBranchOwnCommits
{
    /// <summary>行の左に出す印（アクセント色の縦棒）。</summary>
    public const string Mark = "▍";

    public static IReadOnlyDictionary<string, string> Marks(IEnumerable<string> hashes)
        => hashes.Distinct(StringComparer.Ordinal).ToDictionary(h => h, _ => Mark, StringComparer.Ordinal);

    /// <summary>右クリックの見出し（「このブランチで入れたコミットを選ぶ（3 件・origin/main から）」）。印が無ければ null。</summary>
    public static string? MenuHeader(int count, string? baseRef)
        => count == 0 ? null : $"このブランチで入れたコミットを選ぶ（{count} 件・{baseRef} から）";
}
