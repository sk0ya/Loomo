using System.Security.Cryptography;
using System.Text;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>差分の一覧の 1 ファイルを、もう読んだか（§24.107）。</summary>
public enum DiffViewedState
{
    /// <summary>まだ見ていない（印を付けたことが無い）。</summary>
    None,

    /// <summary>見た。そのときのパッチといまのパッチが同じ。</summary>
    Viewed,

    /// <summary>見たあとに変わった（前に見たときのパッチと、いまのパッチが違う）。</summary>
    ChangedSinceViewed,
}

/// <summary>
/// 差分のファイルに付ける「見た」印の純粋な部分（§24.107・GitLens 19.6.0 の review sessions）。
/// 印は<b>そのファイルのパッチの中身</b>に付ける。コミットのハッシュや行番号ではなく、追加・削除・前後の行だけで照合するので、
/// amend・rebase でコミットが変わっても、上流の変更で行がずれても、中身が同じなら付いたまま。中身が変わったら外れる。
/// </summary>
public static class DiffViewedMarks
{
    /// <summary>1 つの（見方・ファイル）について覚えておく、見たときのパッチの数。見方を行き来しても前の分が残るように少し持つ。</summary>
    public const int MaxFingerprintsPerFile = 4;

    /// <summary>
    /// パッチの指紋。<c>diff --git</c>・<c>index</c>・<c>---</c>／<c>+++</c> の見出しと、<c>@@ -a,b +c,d @@</c> の行番号は捨て、
    /// 塊の中の行（前後・追加・削除・「改行なし」）だけで作る——どれもコミットや位置で変わり、中身の同一性とは関係ないため。
    /// 塊の無いパッチ（バイナリ・モードだけの変更）は全文で作る。
    /// </summary>
    public static string Fingerprint(string patch)
    {
        var body = new StringBuilder();
        var inHunk = false;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                body.Append("@@\n");   // 塊の切れ目は残す（同じ行が別の塊に分かれたのを同じと見ない）
                continue;
            }
            if (!inHunk || line.Length == 0)
                continue;
            if (line[0] is ' ' or '+' or '-' or '\\')
                body.Append(line).Append('\n');
        }
        var source = body.Length > 0 ? body.ToString() : patch.Replace("\r", "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
    }

    /// <summary>
    /// 印を覚える単位の「見方」。同じファイルでも、作業ツリーと分岐点との比較とではパッチが違う——見方ごとに覚えないと、
    /// 片方で見た印がもう片方で「変わった」に化ける。コミット範囲はまとめて 1 つ（rebase でハッシュが変わっても続きから読めるように）。
    /// 比べた相手がファイルでない比較（アドホック比較）は印を付けない（null）。
    /// </summary>
    public static string? Scope(DiffFileItem item, GitCompareBaseSelection? compareBase)
    {
        if (item.IsCompare || string.IsNullOrEmpty(item.FullPath)) return null;
        if (item.CommitFile is not null) return "range";
        if (item.CompareBaseFile is not null)
            return compareBase is { } selection ? $"base:{selection.Kind}:{selection.Target}" : "base";
        if (item.Entry is not null) return "wt";
        return null;
    }

    /// <summary>
    /// いまのパッチの指紋と、覚えている指紋から状態を決める。コミット範囲は見方を 1 つにまとめているので、
    /// 別のコミットの同じファイルを「見たあとに変わった」とは言わない（見た／まだ、だけ）。
    /// </summary>
    public static DiffViewedState StateOf(string? scope, IReadOnlyList<string>? remembered, string fingerprint)
    {
        if (remembered is not { Count: > 0 }) return DiffViewedState.None;
        if (remembered.Contains(fingerprint, StringComparer.Ordinal)) return DiffViewedState.Viewed;
        return scope == "range" ? DiffViewedState.None : DiffViewedState.ChangedSinceViewed;
    }

    /// <summary>
    /// 「次の未読」：<paramref name="current"/> の次から一覧の順に回って、見ていない（変わったものを含む）最初の位置。
    /// 末尾まで来たら先頭へ回る。全部見ていたら -1。
    /// </summary>
    public static int NextUnviewed(IReadOnlyList<DiffViewedState> states, int current)
    {
        for (var step = 1; step <= states.Count; step++)
        {
            var index = ((current < 0 ? -1 : current) + step) % states.Count;
            if (index < 0) index += states.Count;
            if (states[index] != DiffViewedState.Viewed)
                return index;
        }
        return -1;
    }

    /// <summary>一覧の見出しに添える数（「見た 3/12」）。見たものが無ければ空。変わったものがあれば添える。</summary>
    public static string Summary(IReadOnlyList<DiffViewedState> states)
    {
        var viewed = states.Count(s => s == DiffViewedState.Viewed);
        var changed = states.Count(s => s == DiffViewedState.ChangedSinceViewed);
        if (viewed == 0 && changed == 0) return "";
        return changed > 0 ? $"見た {viewed}/{states.Count}・変わった {changed}" : $"見た {viewed}/{states.Count}";
    }
}
