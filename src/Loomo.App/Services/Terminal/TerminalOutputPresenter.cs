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

/// <summary>出力の診断を読む文脈：相対パスの基準と、その回が tasks.json のタスクならそのタスクの problem matcher（§31.17）。</summary>
internal sealed record TerminalOutputMatching(ProblemMatchContext Context, IReadOnlyList<ProblemMatcher>? TaskMatchers = null);

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
        => Present(output, new TerminalOutputMatching(context));

    /// <summary>tasks.json のタスクから走らせた回は、組み込みの前にそのタスクの matcher を当てる（タスクが宣言した読み方が正）。</summary>
    public static TerminalOutputPresentation Present(TerminalCommandOutput output, TerminalOutputMatching matching)
    {
        var context = matching.Context;
        var matchers = matching.TaskMatchers is { Count: > 0 } own ? own.Concat(Matchers).Distinct().ToList() : Matchers;
        var text = output.Text.Replace("\r\n", "\n");
        if (IsGitGraph(output.Command) && GitGraph(text) is { } graph)
            return graph;
        var problems = text.Length == 0 ? [] : ProblemMatcherEngine.Match(text, matchers, context);
        if (Tests(text, problems, context.TaskCwd) is { } tests)
            return tests;
        if (problems.Count > 0)
            return Problems(problems, context);
        if (Json(text) is { } json)
            return json;
        if (Table(output.Command, text) is { } table)
            return table;
        return Plain(text, output.HeadOmitted, output.Styled);
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
    //
    // 読み手はテストの道具ごと（VSTest・Microsoft.Testing.Platform・pytest・jest/vitest）。どれも「集計（まとまりごとの
    // 失敗・合格・スキップ）」と「失敗したテスト（名前・メッセージ・位置）」を同じ形（TestReport）へ読み、描き方は1つ。

    /// <summary>集計1つ（テストのアセンブリ・pytest の実行全体など）。</summary>
    internal sealed record TestSuite(string Name, int Failed, int Passed, int Skipped, string? Duration);

    internal sealed record FailedTest(string Name, string? Time, List<string> Message, List<(string File, int Line)> Locations);

    internal sealed record TestReport(string Tool, List<TestSuite> Suites, List<FailedTest> Failed);

    private static readonly RegexOptions TestOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>テストの出力を読む。どの読み手にも当たらなければ null。</summary>
    internal static TestReport? ReadTests(string text, string cwd)
    {
        var lines = text.Split('\n');
        return VsTest(text, lines) ?? TestingPlatform(lines) ?? Pytest(lines, cwd) ?? Jest(lines, cwd);
    }

    // ── VSTest（dotnet test の既定。日本語と英語）──

    private static readonly Regex VsTestSummary = new(
        @"^(?<ok>成功|失敗|Passed|Failed)!\s*-\s*(?:失敗|Failed):\s*(?<failed>\d+)[、,]\s*(?:合格|Passed):\s*(?<passed>\d+)[、,]\s*"
        + @"(?:スキップ|Skipped):\s*(?<skipped>\d+)[、,]\s*(?:合計|Total):\s*(?<total>\d+)[、,]\s*(?:期間|Duration):\s*(?<duration>.+?)\s*-\s*(?<dll>\S+)",
        TestOptions | RegexOptions.Multiline);

    /// <summary>失敗したテスト1件の見出し行（<c>  失敗 Name [7 ms]</c>）。</summary>
    private static readonly Regex VsTestFailed = new(@"^\s+(?:失敗|Failed)\s+(?<name>\S.*?)\s+\[(?<time>[^\]]+)\]\s*$", TestOptions);

    /// <summary>.NET のスタックトレースの位置（<c>in C:\a.cs:line 12</c> / <c>場所 C:\a.cs:行 12</c>）。</summary>
    private static readonly Regex DotnetLocation = new(@"(?:\bin|場所)\s+(?<file>(?:[A-Za-z]:)?[^:\s][^:]*?\.\w+):(?:line|行)\s*(?<line>\d+)", TestOptions);

    private static TestReport? VsTest(string text, string[] lines)
    {
        var summaries = VsTestSummary.Matches(text);
        var failed = ReadDotnetFailures(lines, VsTestFailed, isSummary: VsTestSummary.IsMatch);
        if (summaries.Count == 0 && failed.Count == 0)
            return null;
        var suites = summaries.Select(m => new TestSuite(Path.GetFileName(m.Groups["dll"].Value), Int(m, "failed"), Int(m, "passed"),
            Int(m, "skipped"), m.Groups["duration"].Value)).ToList();
        return new("dotnet test", suites, failed);
    }

    // ── Microsoft.Testing.Platform（dotnet test の新しい実行系）──

    private static readonly Regex MtpSummary = new(@"^\s*Test run summary:\s*(?<ok>Passed!|Failed!|Zero tests ran)\s*(?:-\s*(?<dll>\S+))?", TestOptions);
    private static readonly Regex MtpCount = new(@"^\s+(?<key>total|failed|succeeded|skipped|duration):\s*(?<value>.+?)\s*$", TestOptions);
    private static readonly Regex MtpFailed = new(@"^\s*failed\s+(?<name>\S.*?)\s+\((?<time>[^)]+)\)\s*$", TestOptions);

    private static TestReport? TestingPlatform(string[] lines)
    {
        var suites = new List<TestSuite>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (MtpSummary.Match(lines[i]) is not { Success: true } head)
                continue;
            int failedCount = 0, passed = 0, skipped = 0;
            string? duration = null;
            for (var j = i + 1; j < lines.Length && MtpCount.Match(lines[j]) is { Success: true } count; j++)
            {
                var value = count.Groups["value"].Value;
                _ = count.Groups["key"].Value switch
                {
                    "failed" => failedCount = ParseInt(value),
                    "succeeded" => passed = ParseInt(value),
                    "skipped" => skipped = ParseInt(value),
                    "duration" => (duration = value).Length,
                    _ => 0,
                };
            }
            var name = head.Groups["dll"].Success ? Path.GetFileName(head.Groups["dll"].Value) : "テスト";
            suites.Add(new TestSuite(name, failedCount, passed, skipped, duration));
        }
        var failed = ReadDotnetFailures(lines, MtpFailed, isSummary: line => MtpSummary.IsMatch(line));
        // 集計の行があるときだけ（「failed … (…)」の行だけでは、ほかの出力と取り違えうる）。
        return suites.Count == 0 ? null : new("dotnet test", suites, failed);
    }

    /// <summary>.NET のテストの失敗（見出し行・メッセージ・スタックトレースの位置）。VSTest と MTP で見出し行だけが違う。</summary>
    private static List<FailedTest> ReadDotnetFailures(string[] lines, Regex head, Func<string, bool> isSummary)
    {
        var failed = new List<FailedTest>();
        FailedTest? current = null;
        var inMessage = false;
        foreach (var line in lines)
        {
            if (head.Match(line) is { Success: true } m)
            {
                current = new FailedTest(m.Groups["name"].Value, m.Groups["time"].Value, [], []);
                failed.Add(current);
                // MTP はメッセージの見出しを置かず、見出し行の直後からメッセージが始まる。
                inMessage = true;
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
            if (isSummary(line))
            {
                current = null;
                continue;
            }
            if (DotnetLocation.Match(line) is { Success: true } at)
            {
                inMessage = false;
                AddLocation(current, at.Groups["file"].Value.Trim(), ParseInt(at.Groups["line"].Value));
            }
            else if (inMessage && trimmed.Length > 0)
            {
                current.Message.Add(trimmed);
            }
        }
        return failed;
    }

    // ── pytest ──

    /// <summary>締めの行（<c>==== 1 failed, 3 passed, 1 skipped in 0.12s ====</c>）。</summary>
    private static readonly Regex PytestSummary = new(@"^=+ (?<parts>(?:\d+ \w+(?:, )?)+) in (?<duration>[\d.]+s)\b.*=+\s*$", TestOptions);
    /// <summary>短い要約の失敗行（<c>FAILED tests/test_x.py::test_add - assert 1 == 2</c>）。</summary>
    private static readonly Regex PytestFailed = new(@"^(?:FAILED|ERROR) (?<name>\S+?)(?: - (?<message>.*))?\s*$", TestOptions);
    /// <summary>トレースバックの位置（<c>tests/test_x.py:12: AssertionError</c>）。</summary>
    private static readonly Regex PytestLocation = new(@"^(?<file>[^\s:][^:]*\.py):(?<line>\d+): \S", TestOptions);

    private static TestReport? Pytest(string[] lines, string cwd)
    {
        var summary = lines.Select(l => PytestSummary.Match(l)).LastOrDefault(m => m.Success);
        var failed = new List<FailedTest>();
        var locations = lines.Select(l => PytestLocation.Match(l)).Where(m => m.Success)
            .Select(m => (File: m.Groups["file"].Value, Line: ParseInt(m.Groups["line"].Value))).ToList();
        foreach (var line in lines)
        {
            if (PytestFailed.Match(line) is not { Success: true } m)
                continue;
            var name = m.Groups["name"].Value;
            var test = new FailedTest(name, null, m.Groups["message"].Success ? [m.Groups["message"].Value] : [], []);
            // 位置はトレースバックから、そのテストのファイルのもの（ノード ID の :: の前）。
            var file = name.Split("::")[0];
            foreach (var at in locations.Where(l => l.File.Replace('\\', '/').EndsWith(file.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                AddLocation(test, Resolve(at.File, cwd), at.Line);
            failed.Add(test);
        }
        // 締めの行があるときだけ（FAILED / ERROR で始まる行はログにもよくある）。
        if (summary is null)
            return null;
        var counts = summary is null ? new Dictionary<string, int>() : Counts(summary.Groups["parts"].Value, ',');
        var suites = summary is null ? new List<TestSuite>()
            : [new TestSuite("pytest", counts.GetValueOrDefault("failed") + counts.GetValueOrDefault("error") + counts.GetValueOrDefault("errors"),
                counts.GetValueOrDefault("passed"), counts.GetValueOrDefault("skipped"), summary.Groups["duration"].Value)];
        return new("pytest", suites, failed);
    }

    // ── jest / vitest ──

    /// <summary>jest の集計（<c>Tests:       1 failed, 2 passed, 3 total</c>）。</summary>
    private static readonly Regex JestSummary = new(@"^Tests:\s+(?<parts>.+?)\s*$", TestOptions);
    /// <summary>vitest の集計（<c> Tests  1 failed | 2 passed (3)</c>）。</summary>
    private static readonly Regex VitestSummary = new(@"^\s*Tests\s{2,}(?<parts>\d+ \w+(?: \| \d+ \w+)*)\s+\(\d+\)\s*$", TestOptions);
    private static readonly Regex JestTime = new(@"^(?:Time:\s+|\s*Duration\s+)(?<duration>[\d.]+\s*m?s)\b", TestOptions);
    /// <summary>jest の失敗の見出し（<c>  ● suite › name</c>）。vitest は <c>FAIL  file > suite > name</c>。</summary>
    private static readonly Regex JestFailed = new(@"^\s*● (?<name>(?!Console\b).+?)\s*$", TestOptions);
    private static readonly Regex VitestFailed = new(@"^\s*FAIL\s+(?<name>\S+\s+>\s+.+?)\s*$", TestOptions);
    /// <summary>JS のスタックトレースの位置（<c>at Object.&lt;anonymous&gt; (src/sum.test.js:4:21)</c>・<c>❯ src/a.test.ts:4:21</c>）。</summary>
    private static readonly Regex JsLocation = new(@"(?:\bat .*?\(|❯\s+)(?<file>[^()\s]+?\.[cm]?[jt]sx?):(?<line>\d+):\d+\)?", TestOptions);

    private static TestReport? Jest(string[] lines, string cwd)
    {
        var jest = lines.Select(l => JestSummary.Match(l)).LastOrDefault(m => m.Success);
        var vitest = lines.Select(l => VitestSummary.Match(l)).LastOrDefault(m => m.Success);
        var failed = new List<FailedTest>();
        FailedTest? current = null;
        foreach (var line in lines)
        {
            if ((JestFailed.Match(line) is { Success: true } j ? j : VitestFailed.Match(line)) is { Success: true } head)
            {
                current = failed.FirstOrDefault(f => f.Name == head.Groups["name"].Value);
                if (current is null)
                    failed.Add(current = new FailedTest(head.Groups["name"].Value, null, [], []));
                continue;
            }
            if (current is null)
                continue;
            if (JestSummary.IsMatch(line) || VitestSummary.IsMatch(line) || line.StartsWith("Test Suites:", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }
            if (JsLocation.Match(line) is { Success: true } at)
            {
                if (!at.Groups["file"].Value.Contains("node_modules", StringComparison.Ordinal))
                    AddLocation(current, Resolve(at.Groups["file"].Value, cwd), ParseInt(at.Groups["line"].Value));
                continue;
            }
            var trimmed = line.Trim();
            // 抜粋のコード（「  4 | …」「    | ^」）は位置のリンクで足りるので、メッセージには入れない。
            if (trimmed.Length > 0 && !Regex.IsMatch(trimmed, @"^(?:>\s*)?\d*\s*\|") && current.Locations.Count == 0)
                current.Message.Add(trimmed);
        }
        if (jest is null && vitest is null)
            return null;   // 集計の行が無ければ jest ではない（「● 」の行は systemctl status 等にもある）
        var counts = jest is not null ? Counts(jest.Groups["parts"].Value, ',') : Counts(vitest!.Groups["parts"].Value, '|');
        var duration = lines.Select(l => JestTime.Match(l)).LastOrDefault(m => m.Success)?.Groups["duration"].Value;
        var suite = new TestSuite(jest is not null ? "jest" : "vitest", counts.GetValueOrDefault("failed"), counts.GetValueOrDefault("passed"),
            counts.GetValueOrDefault("skipped") + counts.GetValueOrDefault("todo"), duration);
        return new(suite.Name, [suite], failed);
    }

    // ── 共通 ──

    /// <summary>「1 failed, 3 passed」を数え直す（語は小文字で）。</summary>
    private static Dictionary<string, int> Counts(string parts, char separator)
        => parts.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(' ', 2))
            .Where(p => p.Length == 2 && int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .GroupBy(p => p[1].ToLowerInvariant(), p => int.Parse(p[0], CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Sum());

    private static void AddLocation(FailedTest test, string file, int line)
    {
        if (!test.Locations.Contains((file, line)))
            test.Locations.Add((file, line));
    }

    private static string Resolve(string file, string cwd)
    {
        if (Path.IsPathRooted(file) || cwd.Length == 0)
            return file;
        try { return Path.GetFullPath(Path.Combine(cwd, file)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return file; }
    }

    private static int Int(Match m, string group) => ParseInt(m.Groups[group].Value);

    private static int ParseInt(string s)
        => int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static TerminalOutputPresentation? Tests(string text, IReadOnlyList<TaskProblem> problems, string cwd)
    {
        if (ReadTests(text, cwd) is not { } report)
            return null;
        var failedCount = report.Suites.Count > 0 ? report.Suites.Sum(s => s.Failed) : report.Failed.Count;
        var passed = report.Suites.Sum(s => s.Passed);
        var skipped = report.Suites.Sum(s => s.Skipped);

        var html = new StringBuilder(Style);
        html.Append("<p class=\"tso-verdict ").Append(failedCount > 0 ? "tso-bad\">✗ 失敗 " + failedCount : "tso-good\">✓ すべて合格")
            .Append("</p>");
        if (report.Suites.Count > 0)
        {
            html.Append("<table class=\"tso-table\"><thead><tr><th>テスト</th><th>失敗</th><th>合格</th><th>スキップ</th><th>期間</th></tr></thead><tbody>");
            foreach (var suite in report.Suites)
            {
                html.Append("<tr><td>").Append(suite.Failed > 0 ? "<span class=\"tso-bad\">✗</span> " : "<span class=\"tso-good\">✓</span> ")
                    .Append(Encode(suite.Name)).Append("</td>")
                    .Append("<td class=\"tso-num\">").Append(suite.Failed).Append("</td>")
                    .Append("<td class=\"tso-num\">").Append(suite.Passed).Append("</td>")
                    .Append("<td class=\"tso-num\">").Append(suite.Skipped).Append("</td>")
                    .Append("<td>").Append(Encode(suite.Duration ?? "")).Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }
        if (report.Failed.Count > 0)
        {
            html.Append("<h3 id=\"tso-failed\">失敗したテスト</h3>");
            foreach (var test in report.Failed)
            {
                html.Append("<details class=\"tso-test\" open><summary><span class=\"tso-bad\">✗</span> ").Append(Encode(test.Name));
                if (test.Time is { Length: > 0 } time)
                    html.Append(" <span class=\"tso-dim\">").Append(Encode(time)).Append("</span>");
                html.Append("</summary>");
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

    private static TerminalOutputPresentation Plain(
        string text, bool headOmitted, IReadOnlyList<IReadOnlyList<Terminal.Tabs.TerminalOutputRun>>? styled)
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
        html.Append("<pre class=\"tso-plain\">");
        // 描かれたときの色で（ターミナルで見た色のまま）。行が文字と揃っていなければ素の文字に戻る。
        if (styled is not null && styled.Count == TerminalCommandOutputs.LineCount(text))
            AppendStyled(html, styled);
        else
            html.Append(Encode(clipped ? text[^MaxPlainChars..] : text));
        html.Append("</pre>");
        var lines = TerminalCommandOutputs.LineCount(text);
        return new(TerminalOutputShape.Plain, $"出力 · {lines} 行", html.ToString());
    }

    /// <summary>行ごとの書式を span にする。上限（<see cref="MaxPlainChars"/>）を超えるなら末尾の行から収まる分だけ。</summary>
    private static void AppendStyled(StringBuilder html, IReadOnlyList<IReadOnlyList<Terminal.Tabs.TerminalOutputRun>> rows)
    {
        var first = rows.Count;
        for (var budget = MaxPlainChars; first > 0; first--)
        {
            budget -= rows[first - 1].Sum(run => run.Text.Length) + 1;
            if (budget < 0)
                break;
        }
        for (var i = first; i < rows.Count; i++)
        {
            foreach (var run in rows[i])
            {
                if (run.IsPlain)
                {
                    html.Append(Encode(run.Text));
                    continue;
                }
                var css = new List<string>();
                if (run.Foreground is { } fg) css.Add($"color:#{fg.R:X2}{fg.G:X2}{fg.B:X2}");
                if (run.Background is { } bg) css.Add($"background:#{bg.R:X2}{bg.G:X2}{bg.B:X2}");
                if (run.Bold) css.Add("font-weight:bold");
                if (run.Dim) css.Add("opacity:.6");
                if (run.Italic) css.Add("font-style:italic");
                var decorations = new List<string>();
                if (run.Underline) decorations.Add("underline");
                if (run.Strikethrough) decorations.Add("line-through");
                if (decorations.Count > 0) css.Add("text-decoration:" + string.Join(' ', decorations));
                if (run.Inverse) css.Add("filter:invert(1)");
                var span = $"<span style=\"{string.Join(';', css)}\">{Encode(run.Text)}</span>";
                html.Append(run.Hyperlink is { Length: > 0 } link ? $"<a href=\"{EncodeAttribute(link)}\">{span}</a>" : span);
            }
            if (i < rows.Count - 1)
                html.Append('\n');
        }
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
