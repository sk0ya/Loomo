using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークツリーへの引き継ぎ（§24.48）の結果を Git ペインの 1 行に。</summary>
internal static class GitWorktreeCarryPresentation
{
    public static string? Summary(GitWorktreeCarryResult result)
    {
        if (result.IsEmpty) return null;
        var parts = new List<string>();
        if (result.Linked.Count > 0) parts.Add($"リンク {string.Join(", ", result.Linked)}");
        if (result.Copied.Count > 0) parts.Add($"コピー {string.Join(", ", result.Copied)}");
        if (result.Failed.Count > 0) parts.Add($"失敗 {string.Join(", ", result.Failed)}");
        if (parts.Count == 0)
            return "引き継ぐものはありませんでした（元に無いか、新しいワークツリーに既にあります）。";
        return "ワークツリーへ引き継ぎました: " + string.Join(" / ", parts);
    }
}
