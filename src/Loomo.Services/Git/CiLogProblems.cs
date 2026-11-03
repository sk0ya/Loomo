using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>CI のログに出たエラー・警告1件。<paramref name="Path"/> は<b>ビルドエージェントの上のパス</b>（手元のパスへは
/// <see cref="CiLogProblems.MapToClone"/> で直す）。<paramref name="Line"/>・<paramref name="Column"/> は 1 始まり（無ければ 0）。</summary>
public sealed record CiLogProblem(string Path, int Line, int Column, string Severity, string Message);

/// <summary>
/// Azure Pipelines・GitHub Actions のログからエラー・警告の場所を拾う（純粋。ブラウザサポート §41）。
/// <para>拾う形は二つだけで、<b>どちらも error／warning の語を伴うもの</b>に限る：MSBuild／tsc の <c>path(12,5): error CS0103: …</c> と
/// gcc 系の <c>path:12:5: error: …</c>。語を求めないと <c>example.com:443</c> のような行まで場所に見えてしまう。
/// 行の頭の時刻（<c>2026-11-03T01:02:03.4567890Z </c>）、<c>gh run view --log-failed</c> の「ジョブ⇥ステップ⇥」、
/// <c>##[error]</c> の印、色の制御文字、MSBuild が末尾に付ける <c>[…\proj.csproj]</c> は外してから読む。</para>
/// </summary>
public static class CiLogProblems
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z\s?", RegexOptions.Compiled);
    private static readonly Regex Marker = new(@"^##\[(?:error|warning)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProjectSuffix = new(@"\s+\[[^\[\]]+\]\s*$", RegexOptions.Compiled);

    private static readonly Regex MsCompile = new(
        @"^(?<file>(?:[A-Za-z]:)?[^:()\t]+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\b\s*(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Gcc = new(
        @"^(?<file>(?:[A-Za-z]:)?[^:()\s]+\.[A-Za-z0-9]+):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<sev>error|warning)\b:?\s*(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<CiLogProblem> Extract(string? log, int max = 200)
    {
        var problems = new List<CiLogProblem>();
        if (string.IsNullOrEmpty(log))
            return problems;
        var seen = new HashSet<(string, int, int, string)>();
        foreach (var raw in log.Split('\n'))
        {
            if (TryParseLine(raw) is not { } problem || !seen.Add((problem.Path, problem.Line, problem.Column, problem.Message)))
                continue;
            problems.Add(problem);
            if (problems.Count >= max)
                break;
        }
        return problems;
    }

    public static CiLogProblem? TryParseLine(string raw)
    {
        var line = Ansi.Replace(raw.TrimEnd('\r'), "");
        // gh run view --log-failed は「ジョブ名⇥ステップ名⇥時刻 本文」。
        if (line.Split('\t') is { Length: >= 3 } columns && Timestamp.IsMatch(columns[2]))
            line = string.Join('\t', columns[2..]);
        line = Timestamp.Replace(line.TrimStart(), "");
        line = Marker.Replace(line, "").Trim();
        line = ProjectSuffix.Replace(line, "");
        var match = MsCompile.Match(line);
        if (!match.Success)
            match = Gcc.Match(line);
        if (!match.Success)
            return null;
        var file = match.Groups["file"].Value.Trim();
        if (file.Contains("://", StringComparison.Ordinal))
            return null;
        return new CiLogProblem(file, Int(match.Groups["line"].Value), Int(match.Groups["col"].Value),
            match.Groups["sev"].Value.ToLowerInvariant(), match.Groups["msg"].Value.Trim());
    }

    /// <summary>
    /// エージェントの上のパス（<c>D:\a\1\s\src\App\Main.cs</c>・<c>/home/runner/work/repo/repo/src/App/Main.cs</c>）を手元の
    /// クローンのファイルへ。<b>後ろから見て一番長く一致する</b>ものを採る：エージェントの作業フォルダーの深さは CI ごとに
    /// 違うので、先頭の段を1つずつ落としながら、クローンの下にそのファイルがあるかを見る。相対パスはクローンの根から。
    /// 見つからなければ null。<paramref name="exists"/> はテストのため（既定は <see cref="File.Exists"/>）。
    /// </summary>
    public static string? MapToClone(string repositoryRoot, string agentPath, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var segments = agentPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[0].EndsWith(':'))
            segments.RemoveAt(0);   // ドライブ
        if (segments.Count == 0 || segments.Any(s => s is ".." or "."))
            return null;
        var root = Path.GetFullPath(repositoryRoot);
        for (var start = 0; start < segments.Count; start++)
        {
            var candidate = Path.Combine([root, .. segments.Skip(start)]);
            if (exists(candidate))
                return candidate;
        }
        return null;
    }

    private static int Int(string text) => int.TryParse(text, out var n) && n > 0 ? n : 0;
}
