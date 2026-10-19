using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Tasks;

namespace sk0ya.Loomo.App.Services;

/// <summary>出力の形（§39.5.1）。見せ方を選ぶ鍵で、EditorSupport の拡張子にあたる。</summary>
internal enum TerminalOutputShape { Plain, GitGraph, Tests, Problems, Json, Table }

/// <summary>見せ直した出力。<see cref="Body"/> はページの本文（body の中身の HTML）、<see cref="Label"/> は頭に出す一言。</summary>
internal sealed record TerminalOutputPresentation(TerminalOutputShape Shape, string Label, string Body);

/// <summary>
/// 実行し終えたコマンドの出力を、<b>形に合わせて見せ直す</b>（§39.5.1）。Claude の会話を読み物にしたのと同じ原理を、
/// ほかの出力へ広げたもの——情報を足すのではなく、ターミナルでは1本の文字列だった同じ中身を、
/// 表は表に、JSON は畳めるツリーに、<c>git log --graph</c> は枝の色分けに、テスト／ビルドは成否と file:line の一覧にする。
/// <para>形の決め方は上から順に：コマンドが <c>git log --graph</c> → テストの結果（<c>dotnet test</c> の集計・失敗行）→
/// ビルドの診断（§31.17 の組み込み problem matcher）→ JSON（全体が1つの値として読める）→ 表（PowerShell の
/// Format-Table の罫線・CSV）→ どれでもなければ素の文字。<b>どれも確かめてから</b>そう見せる——取り違えて崩すより、
/// 素の文字のほうがよい。</para>
/// 純粋関数（I/O なし。problem matcher の実在確認だけ <see cref="ProblemMatchContext.FileExists"/> 経由）。
/// </summary>
internal static class TerminalOutputPresenter
{
    /// <summary>素の文字で出す上限（字）。超えたら末尾を残す（ビルドやテストの結論は末尾に出る）。</summary>
    internal const int MaxPlainChars = 300_000;

    /// <summary>JSON のツリーに描く節の上限。超えた分は「…省略」。</summary>
    internal const int MaxJsonNodes = 4000;

    /// <summary>表に描く行の上限。</summary>
    internal const int MaxTableRows = 2000;

    public static TerminalOutputPresentation Present(TerminalCommandOutput output, ProblemMatchContext context)
    {
        var text = output.Text.Replace("\r\n", "\n");
        if (IsGitGraph(output.Command) && GitGraph(text) is { } graph)
            return graph;
        var problems = text.Length == 0 ? [] : ProblemMatcherEngine.Match(text, Matchers, context);
        if (Tests(text, problems) is { } tests)
            return tests;
        if (problems.Count > 0)
            return Problems(problems, context);
        if (Json(text) is { } json)
            return json;
        if (Table(output.Command, text) is { } table)
            return table;
        return Plain(text, output.HeadOmitted);
    }

    /// <summary>当てる problem matcher（tasks.json を持たない普通のコマンドなので組み込みを全部）。</summary>
    private static readonly IReadOnlyList<ProblemMatcher> Matchers =
    [
        BuiltInProblemMatchers.MsCompile, BuiltInProblemMatchers.Tsc, BuiltInProblemMatchers.Gcc,
        BuiltInProblemMatchers.EslintCompact, BuiltInProblemMatchers.EslintStylish,
    ];

    // ───────── git log --graph ─────────

    internal static bool IsGitGraph(string command)
        => Regex.IsMatch(command, @"^\s*git(\.exe)?\s+(?:-\S+\s+)*log\b", RegexOptions.IgnoreCase)
           && command.Contains("--graph", StringComparison.Ordinal);

    private static readonly Regex GraphPrefix = new(@"^[*|/\\_ .\-]+", RegexOptions.Compiled);
    private static readonly Regex CommitHead = new(@"^(?<hash>[0-9a-f]{7,40})\b(?:\s+\((?<refs>[^)]*)\))?(?<rest>.*)$", RegexOptions.Compiled);

    private static TerminalOutputPresentation? GitGraph(string text)
    {
        var lines = text.Split('\n');
        var html = new StringBuilder("<pre class=\"tso-graph\">");
        var commits = 0;
        foreach (var line in lines)
        {
            var prefix = GraphPrefix.Match(line) is { Success: true } m ? m.Value : "";
            // 罫線の文字は桁ごとに色を変える（git の --graph は2桁で1本の枝）。* は節。
            for (var i = 0; i < prefix.Length; i++)
            {
                var c = prefix[i];
                if (c == ' ')
                    html.Append(' ');
                else
                    html.Append("<span class=\"tso-lane tso-lane-").Append(i / 2 % 6).Append(c == '*' ? " tso-node" : "").Append("\">")
                        .Append(GraphGlyph(c)).Append("</span>");
            }
            var rest = line[prefix.Length..];
            if (prefix.Contains('*') && CommitHead.Match(rest) is { Success: true } head)
            {
                commits++;
                html.Append("<span class=\"tso-hash\">").Append(head.Groups["hash"].Value).Append("</span>");
                if (head.Groups["refs"].Success)
                {
                    foreach (var reference in head.Groups["refs"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        html.Append(" <span class=\"tso-ref\">").Append(Encode(reference)).Append("</span>");
                }
                html.Append(Encode(head.Groups["rest"].Value));
            }
            else
            {
                html.Append(Encode(rest));
            }
            html.Append('\n');
        }
        html.Append("</pre>");
        return commits == 0 ? null : new(TerminalOutputShape.GitGraph, $"枝の図 · {commits} コミット", Style + html);
    }

    /// <summary>枝の文字を描く形。日本語の等幅フォントは <c>\</c> を <c>¥</c> で描くので、罫線は罫線素片で置き換える。</summary>
    private static string GraphGlyph(char c) => c switch
    {
        '*' => "●",
        '|' => "│",
        '/' => "╱",
        '\\' => "╲",
        '-' => "─",
        _ => Encode(c.ToString()),
    };

    // ───────── テストの結果 ─────────

    /// <summary><c>dotnet test</c>（VSTest）の集計行。日本語と英語の両方。</summary>
    private static readonly Regex TestSummary = new(
        @"^(?<ok>成功|失敗|Passed|Failed)!\s*-\s*(?:失敗|Failed):\s*(?<failed>\d+)[、,]\s*(?:合格|Passed):\s*(?<passed>\d+)[、,]\s*"
        + @"(?:スキップ|Skipped):\s*(?<skipped>\d+)[、,]\s*(?:合計|Total):\s*(?<total>\d+)[、,]\s*(?:期間|Duration):\s*(?<duration>.+?)\s*-\s*(?<dll>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>失敗したテスト1件の見出し行（<c>  失敗 Name [7 ms]</c>）。</summary>
    private static readonly Regex FailedTest = new(@"^\s+(?:失敗|Failed)\s+(?<name>\S.*?)\s+\[(?<time>[^\]]+)\]\s*$", RegexOptions.Compiled);

    /// <summary>スタックトレースの位置（<c>in C:\a.cs:line 12</c> / <c>場所 C:\a.cs:行 12</c>）。</summary>
    private static readonly Regex StackLocation = new(@"(?:\bin|場所)\s+(?<file>(?:[A-Za-z]:)?[^:\s][^:]*?\.\w+):(?:line|行)\s*(?<line>\d+)", RegexOptions.Compiled);

    private sealed record FailedTestEntry(string Name, string Time, List<string> Message, List<(string File, int Line)> Locations);

    private static TerminalOutputPresentation? Tests(string text, IReadOnlyList<TaskProblem> problems)
    {
        var summaries = TestSummary.Matches(text);
        var failed = new List<FailedTestEntry>();
        FailedTestEntry? current = null;
        var inMessage = false;
        foreach (var line in text.Split('\n'))
        {
            if (FailedTest.Match(line) is { Success: true } head)
            {
                current = new FailedTestEntry(head.Groups["name"].Value, head.Groups["time"].Value, [], []);
                failed.Add(current);
                inMessage = false;
                continue;
            }
            if (current is null)
                continue;
            var trimmed = line.Trim();
            if (trimmed is "エラー メッセージ:" or "Error Message:")
            {
                inMessage = true;
                continue;
            }
            if (trimmed is "スタック トレース:" or "Stack Trace:")
            {
                inMessage = false;
                continue;
            }
            if (TestSummary.IsMatch(line))
            {
                current = null;
                continue;
            }
            if (StackLocation.Match(line) is { Success: true } at
                && int.TryParse(at.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                var location = (at.Groups["file"].Value.Trim(), number);
                if (!current.Locations.Contains(location))
                    current.Locations.Add(location);
            }
            else if (inMessage && trimmed.Length > 0)
            {
                current.Message.Add(trimmed);
            }
        }
        if (summaries.Count == 0 && failed.Count == 0)
            return null;

        int Sum(string group) => summaries.Sum(m => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture));
        var (failedCount, passed, skipped) = summaries.Count > 0 ? (Sum("failed"), Sum("passed"), Sum("skipped")) : (failed.Count, 0, 0);

        var html = new StringBuilder(Style);
        html.Append("<p class=\"tso-verdict ").Append(failedCount > 0 ? "tso-bad\">✗ 失敗 " + failedCount : "tso-good\">✓ すべて合格")
            .Append("</p>");
        if (summaries.Count > 0)
        {
            html.Append("<table class=\"tso-table\"><thead><tr><th>テスト</th><th>失敗</th><th>合格</th><th>スキップ</th><th>期間</th></tr></thead><tbody>");
            foreach (Match summary in summaries)
            {
                var bad = summary.Groups["failed"].Value != "0";
                html.Append("<tr><td>").Append(bad ? "<span class=\"tso-bad\">✗</span> " : "<span class=\"tso-good\">✓</span> ")
                    .Append(Encode(Path.GetFileName(summary.Groups["dll"].Value))).Append("</td>")
                    .Append("<td class=\"tso-num\">").Append(summary.Groups["failed"].Value).Append("</td>")
                    .Append("<td class=\"tso-num\">").Append(summary.Groups["passed"].Value).Append("</td>")
                    .Append("<td class=\"tso-num\">").Append(summary.Groups["skipped"].Value).Append("</td>")
                    .Append("<td>").Append(Encode(summary.Groups["duration"].Value)).Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }
        if (failed.Count > 0)
        {
            html.Append("<h3 id=\"tso-failed\">失敗したテスト</h3>");
            foreach (var test in failed)
            {
                html.Append("<details class=\"tso-test\" open><summary><span class=\"tso-bad\">✗</span> ")
                    .Append(Encode(test.Name)).Append(" <span class=\"tso-dim\">").Append(Encode(test.Time)).Append("</span></summary>");
                if (test.Message.Count > 0)
                    html.Append("<pre class=\"tso-message\">").Append(Encode(string.Join('\n', test.Message))).Append("</pre>");
                if (test.Locations.Count > 0)
                {
                    html.Append("<div class=\"tso-locations\">");
                    foreach (var (file, line) in test.Locations)
                        html.Append(FileLink(file, line, 0, $"{Path.GetFileName(file)}:{line}")).Append(' ');
                    html.Append("</div>");
                }
                html.Append("</details>");
            }
        }
        if (problems.Count > 0)
        {
            html.Append("<h3 id=\"tso-build\">ビルドの診断</h3>");
            AppendProblems(html, problems, displayPath: null);
        }
        var label = failedCount > 0 ? $"テスト · 失敗 {failedCount} / 合格 {passed}" : $"テスト · 合格 {passed}";
        if (skipped > 0)
            label += $" / スキップ {skipped}";
        return new(TerminalOutputShape.Tests, label, html.ToString());
    }

    // ───────── ビルドの診断 ─────────

    private static TerminalOutputPresentation Problems(IReadOnlyList<TaskProblem> problems, ProblemMatchContext context)
    {
        var errors = problems.Count(p => p.Severity == TaskProblemSeverity.Error);
        var warnings = problems.Count(p => p.Severity == TaskProblemSeverity.Warning);
        var html = new StringBuilder(Style);
        html.Append("<p class=\"tso-verdict ").Append(errors > 0 ? "tso-bad" : "tso-warn").Append("\">")
            .Append(string.Join(" · ", new[] { errors > 0 ? $"✗ エラー {errors}" : null, warnings > 0 ? $"⚠ 警告 {warnings}" : null,
                problems.Count - errors - warnings is > 0 and var others ? $"情報 {others}" : null }.OfType<string>()))
            .Append("</p>");
        AppendProblems(html, problems, path => RelativeTo(path, context.WorkspaceFolder));
        var label = string.Join(" · ", new[] { errors > 0 ? $"エラー {errors}" : null, warnings > 0 ? $"警告 {warnings}" : null }.OfType<string>());
        return new(TerminalOutputShape.Problems, label.Length > 0 ? label : $"診断 {problems.Count}", html.ToString());
    }

    /// <summary>ファイルごとに見出しを立て（アウトライン欄がファイルの目次になる）、重いものから並べる。</summary>
    private static void AppendProblems(StringBuilder html, IReadOnlyList<TaskProblem> problems, Func<string, string>? displayPath)
    {
        var index = 0;
        foreach (var file in problems
                     .GroupBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Min(p => p.Severity))
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            html.Append("<h4 id=\"tso-file-").Append(index++).Append("\">").Append(Encode(displayPath?.Invoke(file.Key) ?? Path.GetFileName(file.Key)))
                .Append(" <span class=\"tso-dim\">").Append(file.Count()).Append("</span></h4><ul class=\"tso-problems\">");
            foreach (var problem in file.OrderBy(p => p.Severity).ThenBy(p => p.Line).ThenBy(p => p.Column))
            {
                var (icon, css) = problem.Severity switch
                {
                    TaskProblemSeverity.Error => ("✗", "tso-bad"),
                    TaskProblemSeverity.Warning => ("⚠", "tso-warn"),
                    _ => ("ℹ", "tso-dim"),
                };
                html.Append("<li><span class=\"").Append(css).Append("\">").Append(icon).Append("</span> ")
                    .Append(FileLink(problem.FilePath, problem.Line, problem.Column,
                        problem.Column > 0 ? $"{problem.Line}:{problem.Column}" : problem.Line.ToString(CultureInfo.InvariantCulture)))
                    .Append(problem.Code is { Length: > 0 } code ? $" <code>{Encode(code)}</code>" : "")
                    .Append(' ').Append(Encode(problem.Message)).Append("</li>");
            }
            html.Append("</ul>");
        }
    }

    private static string RelativeTo(string path, string folder)
    {
        try
        {
            var relative = Path.GetRelativePath(folder, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
        }
        catch (ArgumentException) { return path; }
    }

    // ───────── JSON ─────────

    private static TerminalOutputPresentation? Json(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('{' or '[') || trimmed[^1] is not ('}' or ']'))
            return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var html = new StringBuilder(Style);
            html.Append("<div class=\"tso-json\">");
            var budget = MaxJsonNodes;
            AppendJson(html, null, doc.RootElement, depth: 0, ref budget);
            html.Append("</div>");
            var root = doc.RootElement;
            var label = root.ValueKind == JsonValueKind.Array ? $"JSON · 配列 {root.GetArrayLength()} 件" : $"JSON · {root.EnumerateObject().Count()} 項目";
            return new(TerminalOutputShape.Json, label, html.ToString());
        }
    }

    private static void AppendJson(StringBuilder html, string? key, JsonElement value, int depth, ref int budget)
    {
        if (budget-- <= 0)
        {
            if (budget == -1)
                html.Append("<div class=\"tso-dim\">…省略（").Append(MaxJsonNodes).Append(" 節まで）</div>");
            return;
        }
        var keyHtml = key is null ? "" : $"<span class=\"tso-key\">{Encode(key)}</span>: ";
        switch (value.ValueKind)
        {
            case JsonValueKind.Object or JsonValueKind.Array:
                var isArray = value.ValueKind == JsonValueKind.Array;
                var count = isArray ? value.GetArrayLength() : value.EnumerateObject().Count();
                // 浅いところは開いておく（全体の形が一目で分かる）。深いところは畳む。
                html.Append("<details").Append(depth < 2 ? " open" : "").Append("><summary>").Append(keyHtml)
                    .Append("<span class=\"tso-dim\">").Append(isArray ? $"[{count}]" : $"{{{count}}}").Append("</span></summary><div class=\"tso-children\">");
                if (isArray)
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                        AppendJson(html, (i++).ToString(CultureInfo.InvariantCulture), item, depth + 1, ref budget);
                }
                else
                {
                    foreach (var property in value.EnumerateObject())
                        AppendJson(html, property.Name, property.Value, depth + 1, ref budget);
                }
                html.Append("</div></details>");
                break;
            default:
                var css = value.ValueKind switch
                {
                    JsonValueKind.String => "tso-str",
                    JsonValueKind.Number => "tso-numv",
                    JsonValueKind.True or JsonValueKind.False => "tso-bool",
                    _ => "tso-dim",
                };
                html.Append("<div class=\"tso-leaf\">").Append(keyHtml).Append("<span class=\"").Append(css).Append("\">")
                    .Append(Encode(value.GetRawText())).Append("</span></div>");
                break;
        }
    }

    // ───────── 表 ─────────

    private static readonly Regex DashLine = new(@"^\s*-+(?:\s+-+)*\s*$", RegexOptions.Compiled);

    private static TerminalOutputPresentation? Table(string command, string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var tables = FormatTables(lines);
        if (tables.Count == 0 && Csv(command, lines) is { } csv)
            tables.Add(csv);
        if (tables.Count == 0)
            return null;
        var html = new StringBuilder(Style);
        var rows = 0;
        foreach (var (caption, header, body) in tables)
        {
            if (caption is { Length: > 0 })
                html.Append("<h4>").Append(Encode(caption)).Append("</h4>");
            AppendTable(html, header, body);
            rows += body.Count;
        }
        return new(TerminalOutputShape.Table, $"表 · {rows} 行", html.ToString());
    }

    /// <summary>
    /// PowerShell の Format-Table の形：見出しの行の下に <c>----  ----</c> の罫線。表は空行で終わる。<c>Get-ChildItem</c> の
    /// ように表の前に <c>Directory: …</c> があれば見出しにする。列の切り方は <see cref="ColumnRanges"/>。
    /// </summary>
    internal static List<(string? Caption, string[] Header, List<string[]> Rows)> FormatTables(string[] lines)
    {
        var tables = new List<(string?, string[], List<string[]>)>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (!DashLine.IsMatch(lines[i]) || lines[i - 1].Trim().Length == 0)
                continue;
            var spans = Regex.Matches(lines[i], "-+").Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
            if (spans.Count < 2)
                continue;
            var j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length > 0 && j - i - 1 < MaxTableRows)
                j++;
            if (j == i + 1)
                continue;
            var ranges = ColumnRanges(spans, lines[(i - 1)..j]);
            string[] Cut(string line)
            {
                var cells = new string[ranges.Count];
                for (var c = 0; c < ranges.Count; c++)
                {
                    var start = Math.Min(ranges[c].Start, line.Length);
                    var end = c == ranges.Count - 1 ? line.Length : Math.Min(ranges[c].End, line.Length);
                    cells[c] = end > start ? line[start..end].Trim() : "";
                }
                return cells;
            }
            var header = Cut(lines[i - 1]);
            var rows = lines[(i + 1)..j].Select(Cut).ToList();
            string? caption = null;
            for (var k = i - 2; k >= 0 && k >= i - 4; k--)
            {
                if (lines[k].Trim() is { Length: > 0 } t)
                {
                    caption = t.Contains(':') ? t : null;
                    break;
                }
            }
            tables.Add((caption, header, rows));
            i = j;
        }
        return tables;
    }

    /// <summary>
    /// 列の範囲を決める。罫線の区切りだけでは切れない——PowerShell は数の列を<b>右寄せ</b>にし、見出しより長い値は罫線より
    /// 左へはみ出す（<c>812.21</c> が <c>-----</c> の1桁前から始まる）。そこで「どの行でも空白の桁」を列の隙間とみなして
    /// 塊に分け、各塊をいちばん重なる罫線の列へ寄せる。日時のように中に空白を持つ値（<c>2026/10/19    10:00</c>）は
    /// 2つの塊になるが、同じ罫線に重なるので1列にまとまる。1つの塊が2本の罫線にまたがる（値が隣の列まで詰まっている）
    /// ときだけ、罫線の区切りで割る。
    /// </summary>
    internal static List<(int Start, int End)> ColumnRanges(List<(int Start, int End)> spans, string[] tableLines)
    {
        var width = tableLines.Max(l => l.Length);
        var occupied = new bool[width];
        foreach (var line in tableLines)
        {
            for (var p = 0; p < line.Length; p++)
                occupied[p] |= line[p] != ' ';
        }
        var ranges = spans.Select(_ => (Start: int.MaxValue, End: -1)).ToArray();
        void Add(int column, int start, int end)
            => ranges[column] = (Math.Min(ranges[column].Start, start), Math.Max(ranges[column].End, end));
        for (var p = 0; p < width;)
        {
            if (!occupied[p]) { p++; continue; }
            var start = p;
            while (p < width && occupied[p]) p++;
            var touching = Enumerable.Range(0, spans.Count).Where(c => spans[c].Start < p && spans[c].End > start).ToList();
            if (touching.Count > 1)
            {
                for (var k = 0; k < touching.Count; k++)
                {
                    var c = touching[k];
                    Add(c, k == 0 ? start : spans[c].Start, k == touching.Count - 1 ? p : spans[touching[k + 1]].Start);
                }
                continue;
            }
            var column = touching.Count == 1 ? touching[0]
                : Enumerable.Range(0, spans.Count).OrderBy(c => Math.Min(Math.Abs(spans[c].Start - p), Math.Abs(spans[c].End - start))).First();
            Add(column, start, p);
        }
        // 塊の届かなかった列（値が全部空）は罫線の位置のまま
        for (var c = 0; c < ranges.Length; c++)
        {
            if (ranges[c].End < 0)
                ranges[c] = spans[c];
        }
        return ranges.ToList();
    }

    /// <summary>CSV：2行以上・2列以上で、どの行も列の数が同じ。コマンドが CSV を出すもの（<c>ConvertTo-Csv</c>・
    /// <c>.csv</c> を読むもの）のときだけ見る——カンマを含む普通の文章を表と取り違えない。</summary>
    private static (string?, string[], List<string[]>)? Csv(string command, string[] lines)
    {
        if (!Regex.IsMatch(command, @"ConvertTo-Csv|Export-Csv|\.csv\b|\bcsv\b", RegexOptions.IgnoreCase))
            return null;
        var rows = lines.Where(l => l.Length > 0).Take(MaxTableRows + 1).Select(SplitCsv).ToList();
        if (rows.Count < 2 || rows[0].Length < 2 || rows.Any(r => r.Length != rows[0].Length))
            return null;
        return (null, rows[0], rows.Skip(1).ToList());
    }

    internal static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells.ToArray();
    }

    private static void AppendTable(StringBuilder html, string[] header, List<string[]> rows)
    {
        // 数だけの列は右寄せ（PowerShell の表と同じ）。見出しを押すと並べ替え（ページの共通の仕掛け）。
        var numeric = Enumerable.Range(0, header.Length)
            .Select(c => rows.All(r => c >= r.Length || r[c].Length == 0 || double.TryParse(r[c].Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                         && rows.Any(r => c < r.Length && r[c].Length > 0))
            .ToArray();
        html.Append("<table class=\"tso-table loomo-sortable\"><thead><tr>");
        for (var c = 0; c < header.Length; c++)
            html.Append("<th").Append(numeric[c] ? " class=\"tso-num\"" : "").Append('>').Append(Encode(header[c])).Append("</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            for (var c = 0; c < header.Length; c++)
                html.Append("<td").Append(numeric[c] ? " class=\"tso-num\"" : "").Append('>').Append(Encode(c < row.Length ? row[c] : "")).Append("</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table>");
    }

    // ───────── 素の文字 ─────────

    private static TerminalOutputPresentation Plain(string text, bool headOmitted)
    {
        var html = new StringBuilder(Style);
        if (text.Trim().Length == 0)
        {
            html.Append("<p class=\"tso-dim\">出力はありません。</p>");
            return new(TerminalOutputShape.Plain, "出力なし", html.ToString());
        }
        var clipped = text.Length > MaxPlainChars;
        if (headOmitted || clipped)
            html.Append("<p class=\"tso-dim\">先頭を省略しています（末尾を表示）。全体はエディタで開けます。</p>");
        html.Append("<pre class=\"tso-plain\">").Append(Encode(clipped ? text[^MaxPlainChars..] : text)).Append("</pre>");
        var lines = TerminalCommandOutputs.LineCount(text);
        return new(TerminalOutputShape.Plain, $"出力 · {lines} 行", html.ToString());
    }

    // ───────── 共通 ─────────

    /// <summary>押すとエディタでその行を開くリンク（<c>file:///…:行:桁</c>。振り分けは EditorSupport のプレビューと同じ口）。</summary>
    private static string FileLink(string path, int line, int column, string text)
    {
        string href;
        try { href = new Uri(path).AbsoluteUri; }
        catch (UriFormatException) { href = path; }
        if (line > 0)
            href += ":" + line + (column > 0 ? ":" + column : "");
        return $"<a href=\"{EncodeAttribute(href)}\" title=\"{EncodeAttribute(path)}\">{Encode(text)}</a>";
    }

    private static string Encode(string s) => MarkdownRenderer.Encode(s);
    private static string EncodeAttribute(string s) => MarkdownRenderer.EncodeAttribute(s);

    /// <summary>体裁。色はページのテーマの変数に乗せず、文字色からの透過と固定の成否色で作る（どのテーマでも馴染む）。</summary>
    private const string Style = """
        <style>
        .tso-verdict { font-weight: 600; margin: 0 0 .8em; }
        .tso-good { color: #3fb950; }
        .tso-bad { color: #e5534b; }
        .tso-warn { color: #d29922; }
        .tso-dim { opacity: .6; font-weight: normal; }
        .tso-table { border-collapse: collapse; font-size: .9em; margin: 0 0 1em; }
        .tso-table th, .tso-table td { padding: .2em .7em; border-bottom: 1px solid color-mix(in srgb, currentColor 15%, transparent); text-align: left; }
        .tso-table th { position: sticky; top: 0; background: inherit; }
        .tso-num { text-align: right !important; font-variant-numeric: tabular-nums; }
        .tso-test { margin: .4em 0 .8em; }
        .tso-test summary { cursor: pointer; overflow-wrap: anywhere; }
        .tso-message { white-space: pre-wrap; font-size: .88em; margin: .4em 0 .2em 1.2em; }
        .tso-locations { margin-left: 1.2em; font-size: .88em; }
        .tso-problems { list-style: none; padding-left: .2em; margin: .2em 0 1em; font-size: .9em; }
        .tso-problems li { margin: .15em 0; }
        .tso-json { font-family: var(--loomo-code-font, Consolas, monospace); font-size: .88em; }
        .tso-json summary { cursor: pointer; }
        .tso-children { margin-left: 1.1em; border-left: 1px dotted color-mix(in srgb, currentColor 25%, transparent); padding-left: .6em; }
        .tso-key { opacity: .8; }
        .tso-str { color: #7ee787; }
        .tso-numv { color: #79c0ff; }
        .tso-bool { color: #d2a8ff; }
        .tso-plain, .tso-graph { white-space: pre; overflow-x: auto; font-size: .88em; }
        .tso-hash { color: #d29922; }
        .tso-ref { font-size: .85em; padding: 0 .35em; border-radius: 3px; border: 1px solid color-mix(in srgb, currentColor 35%, transparent); }
        .tso-node { font-weight: bold; }
        .tso-lane-0 { color: #e5534b; } .tso-lane-1 { color: #3fb950; } .tso-lane-2 { color: #d29922; }
        .tso-lane-3 { color: #58a6ff; } .tso-lane-4 { color: #bc8cff; } .tso-lane-5 { color: #39c5cf; }
        table.loomo-sortable th { cursor: pointer; user-select: none; }
        table.loomo-sortable th[data-sort="asc"]::after { content: " ▲"; opacity: .6; }
        table.loomo-sortable th[data-sort="desc"]::after { content: " ▼"; opacity: .6; }
        </style>
        """;
}
