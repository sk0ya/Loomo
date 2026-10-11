using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.Testing;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>pytest の JUnit XML（<see cref="JUnitXmlResultParser"/>）のパース検証。fixture 文字列だけで、pytest は要らない。</summary>
public class JUnitXmlResultParserTests
{
    private const string Root = @"C:\proj";

    private const string Xunit1 = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" errors="0" failures="1" skipped="1" tests="5" time="0.12">
            <testcase classname="tests.test_math" name="test_add" file="tests\test_math.py" line="3" time="0.001" />
            <testcase classname="tests.test_math.TestDiv" name="test_zero" file="tests\test_math.py" line="10" time="0.250">
              <failure message="assert 1 == 2">def test_zero(self):
        &gt;       assert 1 == 2
        E       assert 1 == 2

        tests\helpers.py:4: in check
        tests\test_math.py:12: AssertionError</failure>
            </testcase>
            <testcase classname="tests.test_math" name="test_param[1-2]" file="tests/test_math.py" line="15" time="0.002" />
            <testcase classname="tests.test_math" name="test_param[3-4]" file="tests/test_math.py" line="15" time="0.003">
              <skipped type="pytest.skip" message="later">skipped</skipped>
            </testcase>
            <testcase classname="tests.test_math.TestOuter.TestInner" name="test_deep" file="tests\test_math.py" line="20" time="0" />
          </testsuite>
        </testsuites>
        """;

    private static readonly string MathFile = Path.GetFullPath(@"C:\proj\tests\test_math.py");

    [Fact]
    public void Function_test_maps_file_title_and_zero_based_line()
    {
        var r = JUnitXmlResultParser.Parse(Xunit1, Root)[0];
        Assert.Equal(MathFile, r.FilePath);
        Assert.Equal("test_add", r.Title);
        Assert.Equal(ScriptTestOutcome.Passed, r.Outcome);
        Assert.Equal(4, r.DeclarationLine);   // pytest の line は 0 始まり
        Assert.Null(r.CaseName);
    }

    [Fact]
    public void Class_test_gets_node_id_title_failure_message_and_own_file_location()
    {
        var r = JUnitXmlResultParser.Parse(Xunit1, Root)[1];
        Assert.Equal("TestDiv::test_zero", r.Title);
        Assert.Equal(ScriptTestOutcome.Failed, r.Outcome);
        Assert.Equal("assert 1 == 2", r.Message);
        Assert.Equal(250, r.Duration!.Value.TotalMilliseconds, 3);
        // ヘルパの行より、テストファイル自身の行（アサーション）を優先する。
        Assert.Equal(MathFile, r.FailurePath);
        Assert.Equal(12, r.FailureLine);
    }

    [Fact]
    public void Parametrized_cases_collapse_to_method_title_with_case_name()
    {
        var results = JUnitXmlResultParser.Parse(Xunit1, Root);
        Assert.Equal("test_param", results[2].Title);
        Assert.Equal("test_param[1-2]", results[2].CaseName);
        Assert.Equal(ScriptTestOutcome.Skipped, results[3].Outcome);
        Assert.Equal(MathFile, results[2].FilePath);   // / 区切りの file も同じファイル
    }

    [Fact]
    public void Nested_classes_join_with_double_colon()
        => Assert.Equal("TestOuter::TestInner::test_deep", JUnitXmlResultParser.Parse(Xunit1, Root)[4].Title);

    [Fact]
    public void Without_file_attribute_the_module_is_found_by_existing_py()
    {
        const string xunit2 = """
            <testsuites><testsuite>
              <testcase classname="pkg.tests.test_io.TestRead" name="test_utf8" time="0.01" />
              <testcase classname="pkg.tests.test_io" name="test_top" time="0.01"><error message="boom" /></testcase>
            </testsuite></testsuites>
            """;
        var expected = Path.GetFullPath(@"C:\proj\pkg\tests\test_io.py");
        var results = JUnitXmlResultParser.Parse(xunit2, Root,
            p => string.Equals(Path.GetFullPath(p), expected, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, results[0].FilePath);
        Assert.Equal("TestRead::test_utf8", results[0].Title);
        Assert.Equal("test_top", results[1].Title);
        Assert.Equal(ScriptTestOutcome.Failed, results[1].Outcome);   // error も失敗扱い
        Assert.Equal("boom", results[1].Message);
    }
}

/// <summary>pytest のソース走査（<see cref="PytestDiscovery"/>）。</summary>
public class PytestDiscoveryTests
{
    private static System.Collections.Generic.List<ScriptDiscoveredTest> Parse(string source)
        => PytestDiscovery.ParseSource(@"C:\proj\tests\test_x.py", source);

    [Fact]
    public void Finds_functions_class_methods_and_parametrize()
    {
        var tests = Parse("""
            import pytest

            def helper():
                def test_inner():   # 関数の中の def は拾わない
                    pass

            def test_top():
                assert True

            @pytest.mark.parametrize("a", [1, 2])
            async def test_param(a):
                pass

            class TestThing:
                def setup_method(self):
                    pass

                def test_method(self):
                    pass

                class TestNested:
                    def test_deep(self):
                        pass

            class Helper:
                def test_not_collected(self):
                    pass

            class Legacy(unittest.TestCase):
                def test_legacy(self):
                    pass
            """);

        Assert.Equal(["test_top", "test_param", "TestThing::test_method", "TestThing::TestNested::test_deep",
            "Legacy::test_legacy"], tests.Select(t => t.Title).ToArray());
        Assert.Equal(7, tests[0].Line1);
        Assert.False(tests[0].IsParameterized);
        Assert.True(tests[1].IsParameterized);
        Assert.Equal(11, tests[1].Line1);   // デコレータではなく def の行
    }

    [Fact]
    public void Ignores_defs_inside_docstrings()
    {
        var tests = Parse("""
            '''
            def test_in_docstring():
                pass
            '''
            def test_real():
                pass
            """);
        Assert.Equal("test_real", Assert.Single(tests).Title);
    }
}

/// <summary>ランナーの判定と、ファイル→プロジェクトの解決（一時ディレクトリで実ファイルを置いて確かめる）。</summary>
public sealed class TestRunnerDetectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loomo-testrunner-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Js_config_files_and_jest_key_are_recognized()
    {
        Write(@"a\vitest.config.ts");
        Write(@"b\jest.config.js");
        Write(@"c\package.json", """{ "jest": { "testEnvironment": "node" } }""");
        Write(@"d\package.json", """{ "devDependencies": { "jest": "29" } }""");
        Write(@"d\vitest.config.ts");   // 依存が設定ファイルに勝つ

        Assert.Equal("vitest", JsTestRunnerDetection.Detect(Path.Combine(_root, "a")));
        Assert.Equal("jest", JsTestRunnerDetection.Detect(Path.Combine(_root, "b")));
        Assert.Equal("jest", JsTestRunnerDetection.Detect(Path.Combine(_root, "c")));
        Assert.Equal("jest", JsTestRunnerDetection.Detect(Path.Combine(_root, "d")));
    }

    [Fact]
    public void Pytest_strong_and_weak_markers()
    {
        Write(@"s1\pytest.ini");
        Write(@"s2\pyproject.toml", "[tool.pytest.ini_options]\naddopts = \"-q\"");
        Write(@"s3\setup.cfg", "[tool:pytest]\n");
        Write(@"w1\conftest.py");
        Write(@"w2\requirements-dev.txt", "pytest==8.0\n");
        Write(@"n1\requirements.txt", "requests\n");

        var runner = new PytestRunner();
        Assert.Equal(TestRunnerMatch.Strong, runner.Detect(Path.Combine(_root, "s1")));
        Assert.Equal(TestRunnerMatch.Strong, runner.Detect(Path.Combine(_root, "s2")));
        Assert.Equal(TestRunnerMatch.Strong, runner.Detect(Path.Combine(_root, "s3")));
        Assert.Equal(TestRunnerMatch.Weak, runner.Detect(Path.Combine(_root, "w1")));
        Assert.Equal(TestRunnerMatch.Weak, runner.Detect(Path.Combine(_root, "w2")));
        Assert.Null(runner.Detect(Path.Combine(_root, "n1")));
    }

    [Fact]
    public void Pytest_root_is_nearest_config_else_outermost_weak_marker()
    {
        var test = Write(@"py\tests\unit\test_a.py");
        Write(@"py\tests\conftest.py");
        Write(@"py\pyproject.toml", "[project]\nname = \"x\"");
        var folders = new[] { _root };
        var runners = new ITestRunner[] { new PytestRunner() };

        // 設定が無い → 最も外側の手掛かり（pyproject.toml のある py\）。
        Assert.Equal(Path.Combine(_root, "py"), TestProjectLocator.Find(test, folders, runners)!.Value.ProjectDir);

        // tests\ に pytest.ini があれば、そこが最寄りの強い一致。
        Write(@"py\tests\pytest.ini");
        Assert.Equal(Path.Combine(_root, "py", "tests"),
            TestProjectLocator.Find(test, folders, runners)!.Value.ProjectDir);
    }

    [Fact]
    public void Search_stops_at_the_owning_workspace_folder_in_multi_root()
    {
        // 親（_root）に設定があっても、ワークスペースフォルダー（app）の外へは上らない。
        Write("pytest.ini");
        var app = Path.Combine(_root, "app");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var test = Write(@"app\test_b.py");
        var runners = new ITestRunner[] { new PytestRunner() };

        Assert.Null(TestProjectLocator.Find(test, [other, app], runners));
        Write(@"app\conftest.py");
        Assert.Equal(app, TestProjectLocator.Find(test, [other, app], runners)!.Value.ProjectDir);
    }

    [Fact]
    public void Node_bin_is_found_in_hoisted_node_modules()
    {
        var pkg = Path.Combine(_root, "packages", "web");
        Directory.CreateDirectory(pkg);
        var bin = Write(@"node_modules\.bin\vitest.cmd");
        var tools = new TestToolLocator([_root], resolveOnPath: _ => null);

        Assert.Equal(bin, tools.FindNodeBin(pkg, "vitest"));
        var cmd = new VitestRunner().BuildCommand(new TestRunRequest(pkg, @"C:\r.json", @"src\a.test.ts", "adds (x)"), tools);
        Assert.Contains($"& '{bin}' run --reporter=json", cmd);
        Assert.Contains(@"-t 'adds \(x\)'", cmd);   // -t は正規表現なので括弧をエスケープ
    }
}

/// <summary>pytest のコマンド組み立て（実行ファイルの選択とノード ID）。</summary>
public class PytestCommandTests
{
    [Fact]
    public void Prefers_venv_python_and_passes_node_id_and_rootdir()
    {
        var venv = Path.Combine(@"C:\proj", ".venv", "Scripts", "python.exe");
        var tools = new TestToolLocator([@"C:\proj"], fileExists: p => p == venv, resolveOnPath: _ => null);
        var cmd = new PytestRunner().BuildCommand(
            new TestRunRequest(@"C:\proj", @"C:\t\r.xml", @"tests\test_x.py", "TestA::test_b"), tools);

        Assert.StartsWith(@"Set-Location 'C:\proj'; & 'C:\proj\.venv\Scripts\python.exe' -m pytest", cmd);
        Assert.Contains(@"--junitxml='C:\t\r.xml' -o junit_family=xunit1 --rootdir='C:\proj'", cmd);
        Assert.EndsWith("'tests/test_x.py::TestA::test_b'", cmd);
    }

    [Fact]
    public void Falls_back_to_pytest_on_path_then_py_launcher_then_python()
    {
        var req = new TestRunRequest(@"C:\proj", @"C:\r.xml");
        var runner = new PytestRunner();
        Assert.Contains(@"& 'C:\bin\pytest.exe' --junitxml", runner.BuildCommand(req,
            new TestToolLocator([], _ => false, e => e == "pytest" ? @"C:\bin\pytest.exe" : null)));
        Assert.Contains(@"& 'C:\Windows\py.exe' -m pytest", runner.BuildCommand(req,
            new TestToolLocator([], _ => false, e => e == "py" ? @"C:\Windows\py.exe" : null)));
        Assert.Contains("python -m pytest", runner.BuildCommand(req, new TestToolLocator([], _ => false, _ => null)));
    }
}

/// <summary>結果の一覧への反映（<see cref="ScriptTestRunner.Apply"/>）。パラメータ化のケースはメソッド行へ集約する。</summary>
public class ScriptTestRunnerApplyTests
{
    private static TestItemViewModel Item(string file, string title)
        => new(ScriptTestRunner.MakeFqn(file, title), file, title, title, title);

    [Fact]
    public void Cases_aggregate_and_unknown_tests_are_added_with_declaration_line()
    {
        var file = Path.GetFullPath(@"C:\proj\test_x.py");
        var tests = new ObservableCollection<TestItemViewModel> { Item(file, "test_param") };
        var results = new[]
        {
            new ScriptTestResult(file, "test_param", ScriptTestOutcome.Passed, null, TimeSpan.FromMilliseconds(2), "test_param[1]"),
            new ScriptTestResult(file, "test_param", ScriptTestOutcome.Failed, "assert 0", TimeSpan.FromMilliseconds(3),
                "test_param[2]", file, 9),
            new ScriptTestResult(file, "test_dynamic", ScriptTestOutcome.Passed, null, DeclarationLine: 20),
        };

        ScriptTestRunner.Apply(results, tests, Item);

        Assert.Equal(TestStatus.Failed, tests[0].Status);
        Assert.Equal("assert 0", tests[0].Message);
        Assert.Equal(5, tests[0].Duration!.Value.TotalMilliseconds, 3);
        Assert.Equal(9, tests[0].Line);
        var added = Assert.Single(tests, t => t.MethodName == "test_dynamic");
        Assert.Equal(TestStatus.Passed, added.Status);
        Assert.Equal(20, added.DeclarationLine);
    }

    [Fact]
    public void Skips_reported_for_tests_outside_the_request_keep_their_previous_status()
    {
        // vitest / jest は -t で外れた同じファイルのテストを skipped で報告する。成功済みの行をスキップに化けさせない。
        var file = Path.GetFullPath(@"C:\app\a.test.ts");
        var passed = Item(file, "subs");
        passed.Update(TestStatus.Passed, null, null, 0, null);
        var target = Item(file, "adds");
        target.SetRunning();
        var reallySkipped = Item(file, "later");
        reallySkipped.SetRunning();
        var tests = new ObservableCollection<TestItemViewModel> { passed, target, reallySkipped };

        ScriptTestRunner.Apply(
        [
            new ScriptTestResult(file, "subs", ScriptTestOutcome.Skipped, null),
            new ScriptTestResult(file, "adds", ScriptTestOutcome.Passed, null),
            new ScriptTestResult(file, "later", ScriptTestOutcome.Skipped, null),
            new ScriptTestResult(file, "new", ScriptTestOutcome.Skipped, null),
        ], tests, Item);

        Assert.Equal(TestStatus.Passed, passed.Status);
        Assert.Equal(TestStatus.Passed, target.Status);
        Assert.Equal(TestStatus.Skipped, reallySkipped.Status);   // 依頼した行のスキップはそのまま当てる
        Assert.Equal(TestStatus.Skipped, Assert.Single(tests, t => t.MethodName == "new").Status);
    }
}

/// <summary>失敗だけの再実行（§28.14）：複数の対象を 1 回のコマンドへ並べる組み立てと、プロジェクトごとの段取り。</summary>
public sealed class ScriptTestRerunFailedTests : IDisposable
{
    private static readonly TestToolLocator NoTools = new([], fileExists: _ => false, resolveOnPath: _ => null);
    private readonly string _root = Directory.CreateTempSubdirectory("loomo-rerun-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static TestItemViewModel Item(string file, string title, string selector)
        => new(ScriptTestRunner.MakeFqn(file, title), file, title, title, selector);

    [Fact]
    public void Pytest_passes_every_target_as_a_node_id()
    {
        var cmd = new PytestRunner().BuildCommand(new TestRunRequest(@"C:\proj", @"C:\r.xml", Targets:
        [
            new TestTarget(@"tests\test_a.py", "test_x"),
            new TestTarget(@"tests\test_b.py", "TestB::test_y"),
        ]), NoTools);

        Assert.EndsWith("'tests/test_a.py::test_x' 'tests/test_b.py::TestB::test_y'", cmd);
    }

    [Fact]
    public void Vitest_lists_files_and_joins_names_into_one_escaped_pattern()
    {
        var cmd = new VitestRunner().BuildCommand(new TestRunRequest(@"C:\app", @"C:\r.json", Targets:
        [
            new TestTarget(@"src\a.test.ts", "adds (x)"),
            new TestTarget(@"src\a.test.ts", "subs"),
            new TestTarget(@"src\b.test.ts", "adds (x)"),
        ]), NoTools);

        Assert.Contains("'src/a.test.ts' 'src/b.test.ts' -t", cmd);   // ファイルは重複なし
        Assert.EndsWith(@"-t '(adds \(x\)|subs)'", cmd);              // 名前も重複なし・各名前はエスケープ済み
    }

    [Fact]
    public void Jest_passes_all_files_by_path_and_drops_names_when_a_whole_file_is_requested()
    {
        var cmd = new JestRunner().BuildCommand(new TestRunRequest(@"C:\app", @"C:\r.json", Targets:
        [
            new TestTarget(@"src\a.test.ts", "adds"),
            new TestTarget(@"src\b.test.ts"),
        ]), NoTools);

        Assert.Contains("--runTestsByPath 'src/a.test.ts' 'src/b.test.ts'", cmd);
        Assert.DoesNotContain(" -t ", cmd);   // b を丸ごと走らせるので、-t で削らない
    }

    [Fact]
    public void Single_scope_arguments_still_build_the_same_command()
    {
        var single = new TestRunRequest(@"C:\app", @"C:\r.json", @"src\a.test.ts", "adds");
        var viaTargets = new TestRunRequest(@"C:\app", @"C:\r.json", Targets: [new TestTarget(@"src\a.test.ts", "adds")]);
        Assert.Equal(new VitestRunner().BuildCommand(single, NoTools), new VitestRunner().BuildCommand(viaTargets, NoTools));
        Assert.Empty(new TestRunRequest(@"C:\app", @"C:\r.json").EffectiveTargets);   // 全体
    }

    [Fact]
    public void Plan_groups_failed_tests_by_project_with_project_relative_targets()
    {
        Write(@"py\pytest.ini");
        Write(@"web\package.json", """{ "devDependencies": { "vitest": "2" } }""");
        var pyTest = Write(@"py\tests\test_a.py");
        var webTest = Write(@"web\src\a.test.ts");
        var stray = Write(@"loose\test_c.py");
        var runners = new ITestRunner[] { new VitestRunner(), new JestRunner(), new PytestRunner() };

        var plans = ScriptTestsViewModel.PlanRerun(
            [
                Item(pyTest, "test_x", "test_x"),
                Item(webTest, "suite > adds", "adds"),
                Item(pyTest, "TestA::test_y", "TestA::test_y"),
                Item(stray, "test_z", "test_z"),
            ],
            [_root], runners, out var missing);

        Assert.Equal(2, plans.Count);
        var py = plans.Single(p => p.Runner is PytestRunner);
        Assert.Equal(Path.Combine(_root, "py"), py.ProjectDir);
        Assert.Equal([new TestTarget(@"tests\test_a.py", "test_x"), new TestTarget(@"tests\test_a.py", "TestA::test_y")],
            py.Targets);
        var web = plans.Single(p => p.Runner is VitestRunner);
        Assert.Equal([new TestTarget(@"src\a.test.ts", "adds")], web.Targets);
        Assert.Equal([stray], missing);   // ランナーを特定できないファイルは理由を出すために返す
    }
}
