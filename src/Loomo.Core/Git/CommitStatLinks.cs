using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Git;

/// <summary>
/// git show --stat の出力からファイル統計行を判定し、リンク対象のパスを取り出す純ロジック。
/// </summary>
public static partial class CommitStatLinks
{
    /// <summary>統計行内の表示スパンと、実際に開く相対パス。</summary>
    public readonly record struct StatFileRef(int PathIndex, int PathLength, string NavigatePath);

    [GeneratedRegex(@"^(?<indent>\s+)(?<path>.*?\S)\s+\|\s+(?:Bin\b|\d)")]
    private static partial Regex StatLineRegex();

    /// <summary>1行を解析する。統計行でなければ null。</summary>
    public static StatFileRef? TryParse(string line)
    {
        var match = StatLineRegex().Match(line);
        if (!match.Success) return null;
        var pathGroup = match.Groups["path"];
        var navigatePath = ResolveRenameTarget(Unquote(pathGroup.Value.Trim()));
        if (navigatePath.Length == 0) return null;
        return new StatFileRef(pathGroup.Index, pathGroup.Length, navigatePath);
    }

    private static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;

    private static string ResolveRenameTarget(string path)
    {
        var open = path.IndexOf('{');
        if (open >= 0)
        {
            var arrow = path.IndexOf("=>", open, System.StringComparison.Ordinal);
            var close = arrow >= 0 ? path.IndexOf('}', arrow) : -1;
            if (close < 0) return path;
            var newPart = path[(arrow + 2)..close].Trim();
            return (path[..open] + newPart + path[(close + 1)..]).Replace("//", "/");
        }

        var flat = path.IndexOf("=>", System.StringComparison.Ordinal);
        return flat >= 0 ? path[(flat + 2)..].Trim() : path;
    }
}
