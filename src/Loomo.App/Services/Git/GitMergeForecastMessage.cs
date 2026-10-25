using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブランチのマージ・リベースの前に出す衝突の予告の文面（§24.88）。純関数。
/// 予告は <c>git merge-tree</c> の 3-way の結果で、作業ツリーには触らない。
/// </summary>
internal static class GitMergeForecastMessage
{
    /// <summary>並べるファイル名の上限。超えた分は「ほか N 件」。</summary>
    public const int MaxFiles = 8;

    /// <summary>衝突するファイルの一覧の文。衝突しない・調べられなかったなら null。</summary>
    public static string? DescribeConflicts(GitConflictForecast? forecast)
    {
        if (forecast is not { HasConflicts: true } f)
            return null;
        var lines = f.Files.Take(MaxFiles).Select(path => "  ・" + path);
        var more = f.Files.Count > MaxFiles ? $"\n  ほか {f.Files.Count - MaxFiles} 件" : "";
        return $"⚠ {f.Files.Count} 個のファイルが衝突します：\n{string.Join("\n", lines)}{more}";
    }

    /// <summary>マージの確認文。衝突しないなら null（確認を挟まずにそのまま進める）。</summary>
    public static string? MergeConfirmation(string branch, GitConflictForecast? forecast)
        => DescribeConflicts(forecast) is { } conflicts
            ? $"{branch} をいまのブランチへマージすると、\n{conflicts}\n\nマージして衝突を解きますか？"
            : null;

    /// <summary>
    /// リベースの確認文に添える一文。リベースはコミットを 1 つずつ当て直すので、全体のマージで衝突しなくても途中で
    /// 衝突することがある——だから「衝突しない」とは言わず、衝突すると分かったときだけ添える。
    /// </summary>
    public static string? RebaseNote(GitConflictForecast? forecast)
        => DescribeConflicts(forecast) is { } conflicts
            ? conflicts + "\n（全体を一度にマージしたときの予告。リベースはコミットごとに当てるので、途中で別の衝突が出ることもあります）"
            : null;
}
