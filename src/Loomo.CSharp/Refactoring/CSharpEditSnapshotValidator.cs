using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// 非同期のC#編集計画を適用する直前に、計画作成時の本文と現在の本文を照合する。
/// UIやファイルI/Oは受け持たず、Appが現在本文を取得する関数だけを渡す。
/// </summary>
public static class CSharpEditSnapshotValidator
{
    /// <summary>
    /// すべて一致すればnull、外部変更・stale edit・ワークスペース外なら理由を返す。
    /// 現在本文がnullの場合は「まだ存在しない新規ファイル」として、期待値が空のときだけ許可する。
    /// </summary>
    public static string? Validate(
        IReadOnlyDictionary<string, string>? expectedTexts,
        IReadOnlyList<string> workspaceFolders,
        Func<string, string?> currentText)
    {
        ArgumentNullException.ThrowIfNull(workspaceFolders);
        ArgumentNullException.ThrowIfNull(currentText);
        if (expectedTexts is null || expectedTexts.Count == 0) return null;

        foreach (var (rawPath, expected) in expectedTexts)
        {
            string path;
            try { path = Path.GetFullPath(rawPath); }
            catch (ArgumentException) { return "編集元のパスが不正です。"; }

            if (!WorkspacePaths.Contains(workspaceFolders, path))
                return $"{path}: ワークスペース外の編集元です。";

            var actual = currentText(path);
            if (actual is null)
            {
                if (expected.Length == 0) continue;
                return $"{path}: 編集計画作成後にファイルが見つからなくなりました。";
            }
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                return $"{path}: 編集計画作成後に本文が変更されました。もう一度実行してください。";
        }
        return null;
    }
}
