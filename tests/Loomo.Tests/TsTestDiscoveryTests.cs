using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>TS テストソース走査（<see cref="TsTestDiscovery"/>）のパーサのテスト。</summary>
public class TsTestDiscoveryTests
{
    private static System.Collections.Generic.List<TsTestDiscovery.TsDiscoveredTest> Parse(string source)
        => TsTestDiscovery.ParseSource(@"C:\app\src\sample.test.ts", source);

    [Fact]
    public void Parses_top_level_it_and_test()
    {
        var tests = Parse("""
            it('adds numbers', () => {});
            test("subtracts numbers", () => {});
            """);

        Assert.Equal(2, tests.Count);
        Assert.Equal("adds numbers", tests[0].Title);
        Assert.Equal(1, tests[0].Line1);
        Assert.Equal("subtracts numbers", tests[1].Title);
        Assert.Equal(2, tests[1].Line1);
    }

    [Fact]
    public void Nested_describe_prefixes_titles()
    {
        var tests = Parse("""
            describe('math', () => {
              describe('add', () => {
                it('handles zero', () => {});
              });
              it('outer test', () => {});
            });
            it('top level', () => {});
            """);

        Assert.Equal(3, tests.Count);
        Assert.Equal("math > add > handles zero", tests[0].Title);
        Assert.Equal("math > outer test", tests[1].Title);
        Assert.Equal("top level", tests[2].Title);
    }

    [Fact]
    public void Modifiers_and_each_are_recognized()
    {
        var tests = Parse("""
            it.skip('skipped one', () => {});
            it.each([[1], [2]])('case %i', (n) => {});
            test.only(`template title`, () => {});
            """);

        Assert.Equal(3, tests.Count);
        Assert.Equal("skipped one", tests[0].Title);
        Assert.False(tests[0].IsEach);
        Assert.Equal("case %i", tests[1].Title);
        Assert.True(tests[1].IsEach);
        Assert.Equal("template title", tests[2].Title);
    }

    [Fact]
    public void Ignores_non_test_calls_and_reports_lines()
    {
        var tests = Parse("""
            import { describe, it } from 'vitest';
            const fit = () => {};
            fit();

            describe('suite', () => {
              it('first', () => {
                expect(1).toBe(1);
              });

              it('second', () => {});
            });
            """);

        Assert.Equal(2, tests.Count);
        Assert.Equal("suite > first", tests[0].Title);
        Assert.Equal(6, tests[0].Line1);
        Assert.Equal("suite > second", tests[1].Title);
        Assert.Equal(10, tests[1].Line1);
    }
}

/// <summary>vitest / jest の JSON 結果パースとコマンド組み立て（<see cref="TsTestRunner"/>）のテスト。</summary>
public class TsTestRunnerTests
{
    [Fact]
    public void ParseJson_maps_status_title_and_first_failure_line()
    {
        var json = """
            {
              "testResults": [
                {
                  "name": "C:\\app\\src\\sample.test.ts",
                  "assertionResults": [
                    { "ancestorTitles": ["math", "add"], "title": "handles zero", "status": "passed", "failureMessages": [] },
                    { "ancestorTitles": [], "title": "fails", "status": "failed",
                      "failureMessages": ["AssertionError: expected 1 to be 2\n    at sample.test.ts:5:3"] },
                    { "ancestorTitles": [], "title": "later", "status": "pending", "failureMessages": [] }
                  ]
                }
              ]
            }
            """;

        var results = TsTestRunner.ParseJson(json);

        Assert.Equal(3, results.Count);
        Assert.Equal("math > add > handles zero", results[0].Title);
        Assert.Equal(TestStatus.Passed, results[0].Status);
        Assert.Equal(TestStatus.Failed, results[1].Status);
        Assert.Equal("AssertionError: expected 1 to be 2", results[1].Message);   // 1 行目だけ
        Assert.Equal(TestStatus.Skipped, results[2].Status);
    }

    [Fact]
    public void BuildCommand_quotes_and_scopes()
    {
        var all = TsTestRunner.BuildCommand("vitest", @"C:\app", null, null);
        Assert.StartsWith("Set-Location 'C:\\app'; npx vitest run --reporter=json", all);

        var single = TsTestRunner.BuildCommand("jest", @"C:\my app", @"src\a.test.ts", "it's title");
        Assert.Contains("Set-Location 'C:\\my app'", single);
        Assert.Contains("npx jest --json", single);
        Assert.Contains("'src/a.test.ts'", single);                // ファイルは / 区切り
        Assert.Contains("-t 'it''s title'", single);               // ' は '' にエスケープ
    }

    [Fact]
    public void DetectRunner_prefers_dependencies_then_scripts()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("loomo-tsrunner-").FullName;
        try
        {
            var pkg = System.IO.Path.Combine(dir, "package.json");
            System.IO.File.WriteAllText(pkg, """{ "devDependencies": { "vitest": "^2.0.0" } }""");
            Assert.Equal("vitest", TsTestRunner.DetectRunner(pkg));

            System.IO.File.WriteAllText(pkg, """{ "scripts": { "test": "jest --ci" } }""");
            Assert.Equal("jest", TsTestRunner.DetectRunner(pkg));

            System.IO.File.WriteAllText(pkg, """{ "name": "x" }""");
            Assert.Null(TsTestRunner.DetectRunner(pkg));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
