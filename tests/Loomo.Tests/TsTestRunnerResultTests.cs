using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>vitest / jest の JSON 結果（jest 互換形式）のパース検証。とくに <c>duration</c> の抽出——
/// エディタのガターのツールチップに出す所要時間の TS 側の出所。</summary>
public class TsTestRunnerResultTests
{
    private const string Json = """
    {
      "testResults": [
        {
          "name": "C:/work/suite.test.ts",
          "assertionResults": [
            { "ancestorTitles": ["adder"], "title": "adds", "status": "passed", "duration": 12 },
            { "ancestorTitles": [], "title": "no duration", "status": "passed" },
            { "ancestorTitles": [], "title": "null duration", "status": "skipped", "duration": null },
            { "ancestorTitles": [], "title": "negative", "status": "passed", "duration": -1 },
            { "ancestorTitles": [], "title": "breaks", "status": "failed", "duration": 1500,
              "failureMessages": ["AssertionError: expected 1\n    at foo"] }
          ]
        }
      ]
    }
    """;

    private static TsTestRunner.TsTestResult Find(string title)
        => TsTestRunner.ParseJson(Json).Single(r => r.Title.EndsWith(title, System.StringComparison.Ordinal));

    [Fact]
    public void Reads_duration_in_milliseconds()
        => Assert.Equal(12, Find("adds").Duration!.Value.TotalMilliseconds, 3);

    [Fact]
    public void Ancestor_titles_are_joined_like_the_discovery_side()
        => Assert.Equal("adder > adds", Find("adds").Title);

    [Fact]
    public void Missing_null_or_negative_duration_becomes_null()
    {
        Assert.Null(Find("no duration").Duration);
        Assert.Null(Find("null duration").Duration);
        Assert.Null(Find("negative").Duration);
    }

    [Fact]
    public void Failure_keeps_first_message_line_and_duration()
    {
        var r = Find("breaks");
        Assert.Equal(TestStatus.Failed, r.Status);
        Assert.Equal("AssertionError: expected 1", r.Message);
        Assert.Equal(1500, r.Duration!.Value.TotalMilliseconds, 3);
    }
}
