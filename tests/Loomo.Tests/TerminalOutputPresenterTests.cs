using System;
using System.Linq;
using Xunit;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Tasks;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// §39.5.1 実行し終えたコマンドの出力を形に合わせて見せ直す。形の決め方の順（git log --graph → テスト → 診断 → JSON → 表 →
/// 素の文字）・確かめてからそう見せる（取り違えない）・file:line のリンク・選んだ回への追従を見る。描画（WebView）は対象外。
/// </summary>
public sealed class TerminalOutputPresenterTests
{
    private static readonly ProblemMatchContext Context = new(@"C:\ws", @"C:\ws", _ => true);

    private static TerminalOutputPresentation Present(string command, string text)
        => TerminalOutputPresenter.Present(new TerminalCommandOutput(command, text, 0, DateTime.UtcNow, false), Context);

    [Fact]
    public void PowerShellTable_BecomesATable_WithNumericColumnsRightAligned()
    {
        const string text = """

            NPM(K)    PM(M)      WS(M)     CPU(s)      Id  SI ProcessName
            ------    -----      -----     ------      --  -- -----------
                12     8.21      15.40       0.12    4242   1 pwsh
                30   120.50     200.10      12.75    1234   1 devenv

            """;
        var result = Present("Get-Process", text);
        Assert.Equal(TerminalOutputShape.Table, result.Shape);
        Assert.Equal("表 · 2 行", result.Label);
        Assert.Contains("<th class=\"tso-num\">Id</th>", result.Body);
        Assert.Contains("<td>devenv</td>", result.Body);
        Assert.Contains("loomo-sortable", result.Body);
    }

    [Fact]
    public void RightAlignedValuesWiderThanTheHeader_StayInTheirColumn()
    {
        // 数の列は右寄せで、見出しより長い値は罫線より左へはみ出す（実機の Get-Process で崩れた形）。PowerShell と同じく
        // 値の右端を見出しの右端に揃えて組む。
        static string Row(string npm, string pm, string ws, string cpu, string id, string si, string name)
            => $" {npm,6} {pm,8} {ws,10} {cpu,10} {id,7} {si,3} {name}";
        var text = string.Join("\n",
            Row("NPM(K)", "PM(M)", "WS(M)", "CPU(s)", "Id", "SI", "ProcessName"),
            Row("------", "-----", "-----", "------", "--", "--", "-----------"),
            Row("142", "812.21", "640.40", "1820.12", "14242", "1", "msedgewebview2"),
            Row("9", "4.10", "10.00", "0.12", "6000", "1", "sk0ya.Loomo.Pty.Host"));
        var result = Present("Get-Process", text);
        Assert.Contains("<td class=\"tso-num\">812.21</td>", result.Body);
        Assert.Contains("<td class=\"tso-num\">14242</td>", result.Body);
        Assert.Contains("<td>msedgewebview2</td>", result.Body);
        Assert.Contains("<td>sk0ya.Loomo.Pty.Host</td>", result.Body);
    }

    [Fact]
    public void ChildItemTable_TakesTheDirectoryLineAsCaption()
    {
        const string text = """

                Directory: C:\ws

            Mode                 LastWriteTime         Length Name
            ----                 -------------         ------ ----
            -a---          2026/10/19    10:00           1234 a.txt

            """;
        var result = Present("ls", text);
        Assert.Equal(TerminalOutputShape.Table, result.Shape);
        Assert.Contains(@"<h4>Directory: C:\ws</h4>", result.Body);
        Assert.Contains("<td>a.txt</td>", result.Body);
        Assert.Contains("<td>2026/10/19    10:00</td>", result.Body);   // 中に空白のある値も1列
    }

    [Fact]
    public void Csv_OnlyWhenTheCommandProducesCsv()
    {
        const string text = "Name,Count\n\"a, b\",1\nc,2\n";
        var csv = Present("Get-Process | ConvertTo-Csv", text);
        Assert.Equal(TerminalOutputShape.Table, csv.Shape);
        Assert.Contains("<td>a, b</td>", csv.Body);
        // カンマを含む普通の文章を表と取り違えない
        Assert.Equal(TerminalOutputShape.Plain, Present("echo", text).Shape);
    }

    [Fact]
    public void Json_BecomesATree_DeepLevelsFolded()
    {
        var result = Present("gh api repos/x/y", """{ "name": "loomo", "stars": 3, "owner": { "login": "me", "site": { "url": "u" } } }""");
        Assert.Equal(TerminalOutputShape.Json, result.Shape);
        Assert.Equal("JSON · 3 項目", result.Label);
        Assert.Contains("<span class=\"tso-key\">name</span>: <span class=\"tso-str\">&quot;loomo&quot;</span>", result.Body);
        Assert.Contains("<details open><summary><span class=\"tso-key\">owner</span>", result.Body);
        Assert.Contains("<details><summary><span class=\"tso-key\">site</span>", result.Body);   // 3 段目は畳む
    }

    [Fact]
    public void BrokenJson_StaysPlain()
        => Assert.Equal(TerminalOutputShape.Plain, Present("cat x", "{ \"a\": 1, ").Shape);

    [Fact]
    public void GitLogGraph_ColorsLanes_AndMarksHashesAndRefs()
    {
        const string text = """
            * a1b2c3d (HEAD -> main, origin/main) 変更を足す
            | * e4f5a6b 別の枝
            |/
            * 0f1e2d3 最初
            """;
        var result = Present("git log --graph --oneline", text);
        Assert.Equal(TerminalOutputShape.GitGraph, result.Shape);
        Assert.Equal("枝の図 · 3 コミット", result.Label);
        Assert.Contains("<span class=\"tso-hash\">a1b2c3d</span>", result.Body);
        Assert.Contains("<span class=\"tso-ref\">HEAD -&gt; main</span>", result.Body);
        Assert.Contains("tso-lane-1 tso-node", result.Body);   // 2 本目の枝の節
        Assert.DoesNotContain("\\", result.Body.Split("</style>")[1]);   // \ は日本語の等幅フォントで ¥ に見えるので罫線素片で
        Assert.Contains("╱", result.Body);
        // --graph の無い git log は枝の図にしない
        Assert.NotEqual(TerminalOutputShape.GitGraph, Present("git log --oneline", "a1b2c3d 変更").Shape);
    }

    [Fact]
    public void DotnetTest_Failure_ShowsFailedTests_WithLocations()
    {
        const string text = """
              失敗 sk0ya.Loomo.Tests.FooTests.Bar [7 ms]
              エラー メッセージ:
               Assert.Equal() Failure
            Expected: 1
              スタック トレース:
                 場所 sk0ya.Loomo.Tests.FooTests.Bar() 場所 C:\ws\tests\FooTests.cs:行 42

            失敗!   -失敗:     1、合格:   120、スキップ:     2、合計:   123、期間: 5 s - sk0ya.Loomo.Tests.dll (net10.0)
            """;
        var result = Present("dotnet test", text);
        Assert.Equal(TerminalOutputShape.Tests, result.Shape);
        Assert.Equal("テスト · 失敗 1 / 合格 120 / スキップ 2", result.Label);
        Assert.Contains("sk0ya.Loomo.Tests.FooTests.Bar", result.Body);
        Assert.Contains("Assert.Equal() Failure\nExpected: 1", result.Body);
        Assert.Contains("href=\"file:///C:/ws/tests/FooTests.cs:42\"", result.Body);
        Assert.Contains("<td class=\"tso-num\">120</td>", result.Body);
    }

    [Fact]
    public void DotnetTest_EnglishPass_IsAVerdict()
    {
        var result = Present("dotnet test", "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s - My.Tests.dll (net10.0)");
        Assert.Equal(TerminalOutputShape.Tests, result.Shape);
        Assert.Contains("✓ すべて合格", result.Body);
    }

    [Fact]
    public void BuildDiagnostics_GroupedByFile_LinkToLine()
    {
        const string text = """
            C:\ws\src\A.cs(12,5): error CS1002: ; が必要です [C:\ws\src\App.csproj]
            C:\ws\src\A.cs(3,1): warning CS0168: 変数 'x' は宣言されていますが、使用されていません [C:\ws\src\App.csproj]
            C:\ws\src\B.cs(7,9): warning CS0219: 割り当てられていません [C:\ws\src\App.csproj]
            ビルドに失敗しました。
            """;
        var result = Present("dotnet build", text);
        Assert.Equal(TerminalOutputShape.Problems, result.Shape);
        Assert.Equal("エラー 1 · 警告 2", result.Label);
        // エラーのあるファイルが先・ワークスペースからの相対で見出し
        Assert.True(result.Body.IndexOf(@"src\A.cs", StringComparison.Ordinal) < result.Body.IndexOf(@"src\B.cs", StringComparison.Ordinal));
        Assert.Contains("href=\"file:///C:/ws/src/A.cs:12:5\"", result.Body);
        Assert.Contains("<code>CS1002</code>", result.Body);
    }

    [Fact]
    public void Plain_IsEncoded_AndEmptySaysSo()
    {
        var plain = Present("echo", "<b>そのまま</b>");
        Assert.Equal(TerminalOutputShape.Plain, plain.Shape);
        Assert.Contains("&lt;b&gt;そのまま&lt;/b&gt;", plain.Body);
        Assert.Equal("出力なし", Present("cls", "  \n").Label);
    }

    [Fact]
    public void SplitCsv_HandlesQuotes()
        => Assert.Equal(["a, b", "say \"hi\"", ""], TerminalOutputPresenter.SplitCsv("\"a, b\",\"say \"\"hi\"\"\","));

    // ===== ほかのテストの道具 =====

    [Fact]
    public void TestingPlatform_SummaryAndFailures()
    {
        const string text = """
            failed MyTests.Adds (12ms)
              Assert.AreEqual failed. Expected:<1>. Actual:<2>.
              Stack Trace:
                 at MyTests.Adds() in C:\ws\tests\MyTests.cs:line 10

            Test run summary: Failed! - bin\Debug\net10.0\MyTests.dll (net10.0|x64)
              total: 3
              failed: 1
              succeeded: 2
              skipped: 0
              duration: 1s 234ms
            """;
        var result = Present("dotnet test", text);
        Assert.Equal(TerminalOutputShape.Tests, result.Shape);
        Assert.Equal("テスト · 失敗 1 / 合格 2", result.Label);
        Assert.Contains("MyTests.dll", result.Body);
        Assert.Contains("Assert.AreEqual failed.", result.Body);
        Assert.Contains("href=\"file:///C:/ws/tests/MyTests.cs:10\"", result.Body);
    }

    [Fact]
    public void Pytest_SummaryFailuresAndRelativeLocations()
    {
        const string text = """
            ___________________________ test_add ___________________________

                def test_add():
            >       assert 1 + 1 == 3
            E       assert 2 == 3

            tests/test_math.py:4: AssertionError
            =========================== short test summary info ============================
            FAILED tests/test_math.py::test_add - assert 2 == 3
            ===================== 1 failed, 3 passed, 1 skipped in 0.12s =====================
            """;
        var result = Present("pytest", text);
        Assert.Equal(TerminalOutputShape.Tests, result.Shape);
        Assert.Equal("テスト · 失敗 1 / 合格 3 / スキップ 1", result.Label);
        Assert.Contains("tests/test_math.py::test_add", result.Body);
        Assert.Contains("href=\"file:///C:/ws/tests/test_math.py:4\"", result.Body);   // 実行した場所から解く
    }

    [Fact]
    public void Jest_SummaryFailuresWithoutTheCodeFrame()
    {
        const string text = """
            FAIL src/sum.test.js
              ● sum › adds 1 + 2

                expect(received).toBe(expected) // Object.is equality

                Expected: 4
                Received: 3

                  3 | test('adds 1 + 2', () => {
                > 4 |   expect(sum(1, 2)).toBe(4);
                    |                     ^

                  at Object.<anonymous> (src/sum.test.js:4:21)

            Tests:       1 failed, 2 passed, 3 total
            Time:        0.5 s
            """;
        var result = Present("npx jest", text);
        Assert.Equal(TerminalOutputShape.Tests, result.Shape);
        Assert.Equal("テスト · 失敗 1 / 合格 2", result.Label);
        Assert.Contains("sum › adds 1 + 2", result.Body);
        Assert.Contains("Expected: 4", result.Body);
        Assert.DoesNotContain("toBe(4);", result.Body);   // 抜粋のコードはリンクで足りる
        Assert.Contains("href=\"file:///C:/ws/src/sum.test.js:4\"", result.Body);
    }

    [Fact]
    public void Vitest_Summary()
    {
        const string text = """
             FAIL  src/a.test.ts > math > adds
            AssertionError: expected 3 to be 4
             ❯ src/a.test.ts:5:17

             Test Files  1 failed (1)
                  Tests  1 failed | 4 passed (5)
               Duration  320ms
            """;
        var result = Present("npx vitest run", text);
        Assert.Equal(TerminalOutputShape.Tests, result.Shape);
        Assert.Equal("テスト · 失敗 1 / 合格 4", result.Label);
        Assert.Contains("href=\"file:///C:/ws/src/a.test.ts:5\"", result.Body);
    }

    [Fact]
    public void BulletsAndErrorLines_WithoutATestSummary_AreNotTests()
    {
        // systemctl status の「● 」や、ログの「ERROR …」をテストの結果と取り違えない。
        Assert.Equal(TerminalOutputShape.Plain, Present("systemctl status nginx", "● nginx.service - A high performance web server\n   Active: active (running)").Shape);
        Assert.Equal(TerminalOutputShape.Plain, Present("cat app.log", "ERROR something went wrong\nFAILED step 2").Shape);
    }

    // ===== 色（ANSI） =====

    private static Terminal.Tabs.TerminalOutputRun Run(string text, System.Windows.Media.Color? fg = null, bool bold = false)
        => new(text, fg, null, bold, false, false, false, false, false, null);

    [Fact]
    public void StyledPlainOutput_KeepsTheColors()
    {
        var red = System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B);
        var output = new TerminalCommandOutput("npm run lint", "error  x\nok", 1, DateTime.UtcNow, false,
            Styled: [[Run("error", red, bold: true), Run("  x")], [Run("ok")]]);
        var body = TerminalOutputPresenter.Present(output, Context).Body;
        Assert.Contains("<span style=\"color:#E5534B;font-weight:bold\">error</span>  x\nok", body);

        // 行の数が文字と揃っていなければ素の文字
        var broken = output with { Styled = [[Run("error", red)]] };
        Assert.DoesNotContain("<span style", TerminalOutputPresenter.Present(broken, Context).Body);
    }

    [Fact]
    public void StyledOrNull_DropsUncoloredOutput()
    {
        Assert.Null(TerminalCommandOutput.StyledOrNull([[Run("plain")]]));
        Assert.NotNull(TerminalCommandOutput.StyledOrNull([[Run("plain")], [Run("x", bold: true)]]));
    }

    [Fact]
    public void ClippedOutput_KeepsTheStyledTailInStep()
    {
        var lines = Enumerable.Range(0, 3).Select(i => new string('x', TerminalCommandOutputs.MaxChars / 2) + i).ToList();
        var store = new TerminalCommandOutputs();
        store.Record(new TerminalCommandOutput("big", string.Join('\n', lines), 0, DateTime.UtcNow, false,
            Styled: lines.Select(l => (IReadOnlyList<Terminal.Tabs.TerminalOutputRun>)[Run(l, bold: true)]).ToList()));
        var kept = Assert.Single(store.Latest);
        Assert.Equal(TerminalCommandOutputs.LineCount(kept.Text), kept.Styled!.Count);
        Assert.EndsWith("2", kept.Styled[^1][0].Text);
    }

    // ===== tasks.json の matcher =====

    [Fact]
    public void TaskMatchers_AreAppliedToThatTasksOutput()
    {
        // 組み込みには無い書式（自前のリンタ）を、タスクが宣言した matcher で読む。
        var lint = new ProblemMatcher("mylint",
        [
            new ProblemPattern(new System.Text.RegularExpressions.Regex(@"^LINT (\S+) line (\d+): (.*)$"))
            { File = 1, Line = 2, Message = 3 },
        ]);
        var output = new TerminalCommandOutput("mylint .", "LINT src/a.cs line 7: 名前が長すぎます", 1, DateTime.UtcNow, false);
        Assert.Equal(TerminalOutputShape.Plain, TerminalOutputPresenter.Present(output, Context).Shape);
        var withTask = TerminalOutputPresenter.Present(output, new TerminalOutputMatching(Context, [lint]));
        Assert.Equal(TerminalOutputShape.Problems, withTask.Shape);
        Assert.Contains("名前が長すぎます", withTask.Body);
    }

    // ===== 選んだ回への追従 =====

    private static TerminalSupportSubject Shell(Guid tabId) => new(tabId, "pwsh", @"C:\ws", null, null, null);

    [Fact]
    public void Selection_FollowsNewest_UnlessAnOlderRunWasPicked()
    {
        var tab = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        var first = new TerminalCommandOutput("git status", "clean", 0, t0, false, tab);
        var second = new TerminalCommandOutput("dotnet build", "ok", 0, t0.AddSeconds(5), false, tab);
        var third = new TerminalCommandOutput("ls", "a", 0, t0.AddSeconds(9), false, tab);
        var vm = new TerminalSupportViewModel();

        vm.Update(Shell(tab), [second, first], _ => false, t0.AddSeconds(6));
        Assert.Same(second, vm.SelectedRun?.Output);   // 最新を見せる
        Assert.True(vm.Runs[0].IsSelected);

        vm.Update(Shell(tab), [third, second, first], _ => false, t0.AddSeconds(10));
        Assert.Same(third, vm.SelectedRun?.Output);    // 最新を見ていたので付いていく

        vm.SelectRunCommand.Execute(vm.Runs[2]);       // 古い回を選んで読む
        Assert.Same(first, vm.SelectedRun?.Output);
        var fourth = new TerminalCommandOutput("pwd", "C:\\ws", 0, t0.AddSeconds(12), false, tab);
        vm.Update(Shell(tab), [fourth, third, second, first], _ => false, t0.AddSeconds(13));
        Assert.Same(first, vm.SelectedRun?.Output);    // 読んでいる回から動かさない
        Assert.Single(vm.Runs, r => r.IsSelected);
    }
}
