using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <c>git show --stat</c> の出力からファイル統計行（<c> path/to/file.cs | 10 +++---</c>）を判定し、
/// リンク化するファイルパスの位置と、開くべき（リネーム後の）相対パスを取り出す。
/// 統計行以外（コミットヘッダ・メッセージ本文・「N files changed …」の要約行）は <c>null</c>。
/// </summary>
public static partial class CommitStatLinks
{
    /// <summary>
    /// 統計行のファイル部分を表す。<see cref="PathIndex"/>/<see cref="PathLength"/> は元行内の
    /// 表示用スパン（リネーム表記 <c>{old =&gt; new}</c> をそのまま含む）、
    /// <see cref="NavigatePath"/> は実際に開く相対パス（リネームは新パスへ解決済み）。
    /// </summary>
    public readonly record struct StatFileRef(int PathIndex, int PathLength, string NavigatePath);

    // 行頭の空白 → ファイル名 → " | " → 右側が数値グラフ か "Bin"。要約行（| を持たない）は弾かれる。
    [GeneratedRegex(@"^(?<indent>\s+)(?<path>.*?\S)\s+\|\s+(?:Bin\b|\d)")]
    private static partial Regex StatLineRegex();

    /// <summary>1 行を解析する。統計行でなければ <c>null</c>。</summary>
    public static StatFileRef? TryParse(string line)
    {
        var m = StatLineRegex().Match(line);
        if (!m.Success) return null;
        var g = m.Groups["path"];
        var navigate = ResolveRenameTarget(Unquote(g.Value.Trim()));
        if (navigate.Length == 0) return null;
        return new StatFileRef(g.Index, g.Length, navigate);
    }

    /// <summary>git がクォートしたパス（<c>"…"</c>）の外側の引用符だけ外す。</summary>
    private static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;

    /// <summary>
    /// リネーム表記を開くべき新パスへ畳む。
    /// <c>dir/{old =&gt; new}/name</c> → <c>dir/new/name</c>、<c>old =&gt; new</c> → <c>new</c>。
    /// リネームでなければそのまま。
    /// </summary>
    private static string ResolveRenameTarget(string path)
    {
        var open = path.IndexOf('{');
        if (open >= 0)
        {
            var arrow = path.IndexOf("=>", open, System.StringComparison.Ordinal);
            var close = arrow >= 0 ? path.IndexOf('}', arrow) : -1;
            if (close < 0) return path;
            var newPart = path[(arrow + 2)..close].Trim();
            var combined = path[..open] + newPart + path[(close + 1)..];
            return combined.Replace("//", "/");
        }

        var flat = path.IndexOf("=>", System.StringComparison.Ordinal);
        return flat >= 0 ? path[(flat + 2)..].Trim() : path;
    }
}
