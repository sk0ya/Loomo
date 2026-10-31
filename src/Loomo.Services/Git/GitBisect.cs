using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>bisect の判定（§24.100）。</summary>
public enum GitBisectVerdict { Good, Bad, Skip }

/// <summary>bisect の 1 歩の結果。<see cref="Found"/> があれば犯人が見つかった。</summary>
public sealed record GitBisectStep
{
    /// <summary>これから試すコミット（git がチェックアウトした先）。</summary>
    public string? CurrentHash { get; init; }
    public string? CurrentSubject { get; init; }
    /// <summary>このあと試す残りの版と、だいたいの歩数（git の「roughly N steps」）。</summary>
    public int? Remaining { get; init; }
    public int? Steps { get; init; }
    /// <summary>最初に悪くなったコミット。</summary>
    public string? Found { get; init; }
    /// <summary>飛ばした版ばかりが残って一つに絞れなかったとき、候補のコミット。</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    public bool IsFinished => Found is not null || Candidates.Count > 0;
}

/// <summary>
/// <c>git bisect start／good／bad／skip</c> の出力を読む（§24.100）。純関数。英語の出力を読む（日本語訳の数の行も拾う）。
/// 読めなければ空の <see cref="GitBisectStep"/>——呼び出し側は「bisect 中」とだけ出す。
/// </summary>
public static partial class GitBisectParser
{
    public static GitBisectStep Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return new GitBisectStep();
        var text = output.Replace("\r\n", "\n");

        var found = FoundRegex().Match(text);
        if (found.Success)
            return new GitBisectStep { Found = found.Groups["hash"].Value };

        if (text.Contains("only 'skip'ped commits left", StringComparison.OrdinalIgnoreCase))
        {
            var after = text[(text.IndexOf("could be any of", StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? i : 0)..];
            var candidates = HashLineRegex().Matches(after).Select(m => m.Groups["hash"].Value).Distinct().ToList();
            return new GitBisectStep { Candidates = candidates };
        }

        var step = new GitBisectStep();
        var count = RemainingRegex().Match(text);
        if (count.Success)
            step = step with
            {
                Remaining = int.Parse(count.Groups["left"].Value),
                Steps = count.Groups["steps"].Success ? int.Parse(count.Groups["steps"].Value) : null,
            };
        var current = CurrentRegex().Match(text);
        if (current.Success)
            step = step with { CurrentHash = current.Groups["hash"].Value, CurrentSubject = current.Groups["subject"].Value.Trim() };
        return step;
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-f]{7,64}) is the first bad commit", RegexOptions.Multiline)]
    private static partial Regex FoundRegex();

    [GeneratedRegex(@"(?<left>\d+) revisions? left to test after this(?: \(roughly (?<steps>\d+) steps?\))?")]
    private static partial Regex RemainingRegex();

    [GeneratedRegex(@"^\[(?<hash>[0-9a-f]{7,64})\] (?<subject>.*)$", RegexOptions.Multiline)]
    private static partial Regex CurrentRegex();

    [GeneratedRegex(@"^(?<hash>[0-9a-f]{7,64})\b", RegexOptions.Multiline)]
    private static partial Regex HashLineRegex();

    /// <summary>ターミナルで最後に走らせたコマンドの終了コードを判定にする（<c>git bisect run</c> と同じ決まり）：
    /// 0 は良い・125 は飛ばす・それ以外は悪い。分からなければ null。</summary>
    public static GitBisectVerdict? VerdictFromExitCode(int? exitCode) => exitCode switch
    {
        null => null,
        0 => GitBisectVerdict.Good,
        125 => GitBisectVerdict.Skip,
        _ => GitBisectVerdict.Bad,
    };
}
