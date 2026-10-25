using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 差分を「この語に触った変更」だけに絞る（§24.85・WinMerge 2.16.57 の display line filter）。比較そのもの（<see cref="DiffOptions"/>）には触れず、
/// <b>追加・削除の行に語を含むファイル</b>だけを一覧に残す。文脈行（変えていない行）に語があるだけのファイルは残さない——
/// 「<c>Logger</c> に触った変更だけを読む」が目的で、<c>Logger</c> を使っているファイル全部ではない。大文字小文字は区別しない。
/// </summary>
internal static class DiffChangeFilter
{
    /// <summary>git のパッチ（unified）の追加・削除の行に <paramref name="term"/> があるか。ファイルの見出し（<c>+++</c>／<c>---</c>）は数えない。</summary>
    public static bool PatchMatches(string patch, string term)
    {
        if (string.IsNullOrEmpty(term))
            return true;
        var inHunk = false;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                continue;
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }
            if (!inHunk || line.Length == 0 || line[0] is not ('+' or '-'))
                continue;
            if (line.AsSpan(1).Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>素材 2 つの比較（アドホック比較）の追加・削除の行に <paramref name="term"/> があるか。</summary>
    public static bool TextsMatch(string left, string right, string term, DiffOptions? options = null)
    {
        if (string.IsNullOrEmpty(term))
            return true;
        foreach (var line in DiffUtil.Compute(left, right, context: 0, options))
        {
            if (line.Kind is DiffLineKind.Added && line.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
            if (line.Kind is DiffLineKind.Removed && line.LeftText.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
