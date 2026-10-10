using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Tasks;

/// <summary>matcher を当てるときの文脈。相対パスの基準になる2つのフォルダーと、自動判定用の実在確認。</summary>
/// <param name="WorkspaceFolder">タスクを定義したワークスペースフォルダー（<c>${workspaceFolder}</c>）。</param>
/// <param name="TaskCwd">タスクを実行した作業フォルダー（<c>${cwd}</c>）。</param>
/// <param name="FileExists">autoDetect の実在確認（テストで差し替える）。null なら <see cref="File.Exists"/>。</param>
public sealed record ProblemMatchContext(string WorkspaceFolder, string TaskCwd, Func<string, bool>? FileExists = null);

/// <summary>
/// タスクの出力全文へ problem matcher を当て、<see cref="TaskProblem"/> の一覧にする純粋関数（設計書 §31.17）。
///
/// <para><b>可視ターミナルの折り返し。</b>タスクは人間の可視ターミナルで走らせ、その出力を OSC 133 の区切りで
/// 受け取る。ところがターミナルが返す本文は<b>画面の1行ごと</b>で、画面幅で折り返された1行は2行に割れて届く
/// （Terminal の <c>ShellCommandOutputEventArgs.Output</c> の仕様）。MSBuild の診断は絶対パス＋末尾の
/// プロジェクトパスで画面幅を超えるのがふつうなので、割れたままだとメッセージが途中で切れる。
/// そこで <see cref="RejoinSoftWraps"/> で「画面幅ちょうどまで埋まった行」を次の行と継ぎ直す（下の注記）。</para>
/// </summary>
public static class ProblemMatcherEngine
{
    /// <summary>これより狭い「最長行」は画面幅とみなさない（折り返しの継ぎ直しをしない）。
    /// 実際のターミナル幅は 80 桁以上が普通で、短い出力の最長行を画面幅と取り違えないための下限。</summary>
    internal const int MinWrapWidth = 60;

    public static IReadOnlyList<TaskProblem> Match(
        string output, IReadOnlyList<ProblemMatcher> matchers, ProblemMatchContext context, bool rejoinSoftWraps = true)
    {
        if (matchers.Count == 0 || string.IsNullOrEmpty(output)) return [];
        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (rejoinSoftWraps)
            lines = RejoinSoftWraps(lines, line => matchers.Any(m => m.Patterns.Any(p => p.Regex.IsMatch(line))));

        var problems = new List<TaskProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var matcher in matchers)
        {
            foreach (var problem in MatchOne(lines, matcher, context))
            {
                // MSBuild はサマリ節で同じ診断を再掲し、マルチターゲットでも同じ行が2回出る。
                if (seen.Add($"{problem.FilePath}|{problem.Line}|{problem.Column}|{problem.Severity}|{problem.Code}|{problem.Message}"))
                    problems.Add(problem);
            }
        }
        return problems;
    }

    /// <summary>
    /// 画面幅で割れた行を継ぎ直す。画面幅そのものは分からないので、出力の<b>最長行の長さ</b>を幅とみなす
    /// ——折り返しが1回でも起きていれば、割れた前半はちょうど画面幅まで埋まっている（ターミナルは行末の空白を
    /// 削って返すので、それ以上長い行は存在しない）。継ぐのは次の条件が全部そろったときだけ：
    /// <list type="bullet">
    /// <item>この行が最長行と同じ長さ（かつ <see cref="MinWrapWidth"/> 以上）。</item>
    /// <item>次の行が空でなく、空白で始まらない（字下げで始まる行は eslint の指摘行や gcc の抜粋で、続きではない）。</item>
    /// <item>次の行それ自体がどの matcher の行にも当たらない（新しい診断の始まりを前の行に食わせない）。</item>
    /// </list>
    /// 折り返しが起きていない出力で最長行がたまたま上の条件を満たすと誤って継ぐが、その場合も位置（ファイル・行）は
    /// 前半の行から取れるので、壊れるのはメッセージの末尾だけ。継がない場合の損失（全診断のメッセージ切れ）より小さい。
    /// </summary>
    internal static string[] RejoinSoftWraps(string[] lines, Func<string, bool> startsRecord)
    {
        var width = 0;
        foreach (var line in lines) width = Math.Max(width, line.Length);
        if (width < MinWrapWidth) return lines;

        var result = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var current = lines[i];
            var lastRow = lines[i];
            while (lastRow.Length == width && i + 1 < lines.Length &&
                   lines[i + 1].Length > 0 && !char.IsWhiteSpace(lines[i + 1][0]) &&
                   !startsRecord(lines[i + 1]))
            {
                lastRow = lines[++i];
                current += lastRow;
            }
            result.Add(current);
        }
        return result.ToArray();
    }

    private static IEnumerable<TaskProblem> MatchOne(string[] lines, ProblemMatcher matcher, ProblemMatchContext context)
    {
        var patterns = matcher.Patterns;
        if (patterns.Count == 0) yield break;
        var last = patterns[^1];

        // 複数行パターンの途中経過：前の行までで読んだ値と、次に当てるパターンの番号。
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = 0;
        var looping = false;

        foreach (var line in lines)
        {
            if (looping)
            {
                var loopMatch = last.Regex.Match(line);
                if (loopMatch.Success)
                {
                    var merged = new Dictionary<string, string>(data, StringComparer.Ordinal);
                    Collect(last, loopMatch, merged);
                    if (Build(merged, matcher, context) is { } looped) yield return looped;
                    continue;
                }
                looping = false;
                next = 0;
                data.Clear();
            }

            if (next > 0)
            {
                var match = patterns[next].Regex.Match(line);
                if (match.Success)
                {
                    if (next == patterns.Count - 1 && patterns[next].Loop)
                    {
                        // 最初の1行目も繰り返しの1回目として出す。以後は looping で同じ扱い。
                        var merged = new Dictionary<string, string>(data, StringComparer.Ordinal);
                        Collect(patterns[next], match, merged);
                        if (Build(merged, matcher, context) is { } first) yield return first;
                        looping = true;
                        continue;
                    }
                    Collect(patterns[next], match, data);
                    next++;
                    if (next == patterns.Count)
                    {
                        if (Build(data, matcher, context) is { } done) yield return done;
                        next = 0;
                        data.Clear();
                    }
                    continue;
                }
                // 途切れた：この行を1つ目のパターンから当て直す。
                next = 0;
                data.Clear();
            }

            var head = patterns[0].Regex.Match(line);
            if (!head.Success) continue;
            Collect(patterns[0], head, data);
            if (patterns.Count == 1)
            {
                if (Build(data, matcher, context) is { } single) yield return single;
                data.Clear();
            }
            else
            {
                next = 1;
            }
        }
    }

    private static void Collect(ProblemPattern pattern, Match match, Dictionary<string, string> data)
    {
        void Take(string key, int group)
        {
            if (group <= 0 || group >= match.Groups.Count) return;
            var g = match.Groups[group];
            if (g.Success && g.Value.Length > 0) data[key] = g.Value;
        }

        Take("file", pattern.File);
        Take("location", pattern.Location);
        Take("line", pattern.Line);
        Take("column", pattern.Column);
        Take("endLine", pattern.EndLine);
        Take("endColumn", pattern.EndColumn);
        Take("severity", pattern.Severity);
        Take("code", pattern.Code);
        if (pattern.Message is { } message)
        {
            if (message == 0) data["message"] = match.Value;
            else Take("message", message);
        }
    }

    private static TaskProblem? Build(Dictionary<string, string> data, ProblemMatcher matcher, ProblemMatchContext context)
    {
        if (!data.TryGetValue("file", out var file) || string.IsNullOrWhiteSpace(file)) return null;
        if (!data.TryGetValue("message", out var message) || string.IsNullOrWhiteSpace(message)) return null;

        int line = 0, column = 0, endLine = 0, endColumn = 0;
        if (data.TryGetValue("location", out var location))
        {
            var parts = location.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 1) int.TryParse(parts[0], out line);
            if (parts.Length >= 2) int.TryParse(parts[1], out column);
            if (parts.Length >= 4)
            {
                int.TryParse(parts[2], out endLine);
                int.TryParse(parts[3], out endColumn);
            }
        }
        if (data.TryGetValue("line", out var l)) int.TryParse(l, out line);
        if (data.TryGetValue("column", out var c)) int.TryParse(c, out column);
        if (data.TryGetValue("endLine", out var el)) int.TryParse(el, out endLine);
        if (data.TryGetValue("endColumn", out var ec)) int.TryParse(ec, out endColumn);
        line = Math.Max(line, 1);
        column = Math.Max(column, 1);
        if (endLine < line) endLine = line;
        if (endLine == line && endColumn < column) endColumn = column;

        var severity = data.TryGetValue("severity", out var s) ? ParseSeverity(s) : null;
        var path = ResolvePath(file.Trim(), matcher, context);
        if (path is null) return null;
        data.TryGetValue("code", out var code);
        return new TaskProblem(path, line, column, endLine, endColumn,
            severity ?? matcher.DefaultSeverity ?? TaskProblemSeverity.Error,
            string.IsNullOrWhiteSpace(code) ? null : code.Trim(), message.Trim(), matcher.Owner);
    }

    /// <summary>重要度の語を読む。読めない語は null（matcher の既定へ倒す）。</summary>
    internal static TaskProblemSeverity? ParseSeverity(string text)
    {
        var word = text.Trim().ToLowerInvariant();
        if (word.StartsWith("fatal", StringComparison.Ordinal)) return TaskProblemSeverity.Error;
        return word switch
        {
            "error" or "err" or "e" => TaskProblemSeverity.Error,
            "warning" or "warn" or "w" => TaskProblemSeverity.Warning,
            "info" or "information" or "note" or "i" => TaskProblemSeverity.Information,
            "hint" => TaskProblemSeverity.Hint,
            _ => null,
        };
    }

    /// <summary>
    /// matcher が拾ったパスを絶対パスにする。基準は matcher の <c>fileLocation</c> に従う
    /// （<c>${workspaceFolder}</c>＝タスクを定義したフォルダー、<c>${cwd}</c>＝タスクの作業フォルダー）。
    /// <b>どのモードでも、出力が既に絶対パスならそのまま使う</b>——relative 指定の matcher に MSBuild の
    /// 絶対パスが混ざっても、つなげて壊れたパスにしない。
    /// </summary>
    internal static string? ResolvePath(string file, ProblemMatcher matcher, ProblemMatchContext context)
    {
        file = file.Trim().Trim('"');
        if (file.Length == 0) return null;
        try
        {
            if (Path.IsPathRooted(file) && !IsDriveRelative(file))
                return Path.GetFullPath(file);
            var baseDir = ExpandBase(matcher.FileLocationBase, context);
            var candidate = Path.GetFullPath(Path.Combine(baseDir, file));
            if (matcher.FileLocation != ProblemFileLocation.AutoDetect) return candidate;

            var exists = context.FileExists ?? File.Exists;
            if (exists(candidate)) return candidate;
            // 自動判定：基準で見つからなければ、もう一方の基準（作業フォルダー／ワークスペースフォルダー）も試す。
            foreach (var other in new[] { context.TaskCwd, context.WorkspaceFolder })
            {
                var alt = Path.GetFullPath(Path.Combine(other, file));
                if (exists(alt)) return alt;
            }
            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;   // パスとして読めない文字列（matcher の当たり過ぎ）は捨てる
        }
    }

    /// <summary><c>C:foo</c> のようなドライブ相対は絶対扱いしない（基準と結合する）。</summary>
    private static bool IsDriveRelative(string path)
        => path.Length >= 2 && path[1] == ':' && (path.Length == 2 || (path[2] != '\\' && path[2] != '/'));

    private static string ExpandBase(string raw, ProblemMatchContext context)
    {
        var expanded = raw
            .Replace("${workspaceFolder}", context.WorkspaceFolder, StringComparison.Ordinal)
            .Replace("${workspaceRoot}", context.WorkspaceFolder, StringComparison.Ordinal)
            .Replace("${cwd}", context.TaskCwd, StringComparison.Ordinal);
        if (expanded.Contains("${", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(expanded))
            return context.WorkspaceFolder;
        return Path.IsPathRooted(expanded) ? expanded : Path.Combine(context.WorkspaceFolder, expanded);
    }
}
