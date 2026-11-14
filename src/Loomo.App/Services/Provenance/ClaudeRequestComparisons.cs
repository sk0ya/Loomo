using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 頼みごと単位の差分（§43.6 A3）を Diff ペインの素材（<see cref="DiffComparison"/>）にする。左が頼みごとの前、右が後。
/// <b>あとから変わったファイル</b>・<b>前後を組めず抜粋になったファイル</b>は、一覧の見出しと右の題で言う——差分に出ているのは
/// 「その頼みごとがしたこと」で、いまのファイルとは限らないため。「見た」印（§24.107）は頼みごとごとの見方で覚える。
/// </summary>
internal static class ClaudeRequestComparisons
{
    public static IReadOnlyList<DiffComparison> Build(
        IReadOnlyList<ClaudeRequestFileDiff> files, ClaudeWrite origin, Func<string, string> displayPath)
    {
        var scope = $"request:{origin.SessionId}:{origin.PromptLine}";
        return files
            .OrderBy(file => displayPath(file.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var (added, removed) = DiffUtil.Stat(file.Before, file.After);
                return new DiffComparison(LeftTitle(file), file.Before, RightTitle(file), file.After, file.FilePath)
                {
                    Label = displayPath(file.FilePath) + Note(file),
                    Badge = file.Created ? "追加" : "",
                    Stat = (added, removed),
                    ViewedScope = scope,
                };
            })
            .ToList();
    }

    private static string LeftTitle(ClaudeRequestFileDiff file)
        => !file.Exact ? "置換前（抜粋）" : file.Created ? "頼みごとの前（無かった）" : "頼みごとの前";

    private static string RightTitle(ClaudeRequestFileDiff file)
        => !file.Exact ? "置換後（抜粋・行番号はファイルのものではない）"
            : file.Current switch
            {
                ClaudeRequestFileState.Unchanged => "頼みごとの後（いまもこのまま）",
                ClaudeRequestFileState.ChangedLater =>
                    $"頼みごとの後（このあと変わった・足した {file.AddedLines} 行のうち {file.AddedLinesPresent} 行がいまも残る）",
                ClaudeRequestFileState.Missing => "頼みごとの後（いまは無い）",
                _ => "頼みごとの後",
            };

    /// <summary>一覧の見出しに添える短い注記。そのまま読んでよいファイル（いまも同じ）には何も付けない。</summary>
    private static string Note(ClaudeRequestFileDiff file)
        => file.Current == ClaudeRequestFileState.Missing ? "  ※いまは無い"
            : !file.Exact ? "  ※抜粋"
            : file.Current == ClaudeRequestFileState.ChangedLater ? "  ※このあと変わった"
            : "";
}
