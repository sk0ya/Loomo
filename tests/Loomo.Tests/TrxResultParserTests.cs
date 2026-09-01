using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.CSharp.Testing;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>TRX（VSTest 形式 XML）パーサの検証。とくに <c>duration</c> の解釈——
/// エディタのガターのツールチップに出す所要時間の出所なので、欠落と書式を固定しておく。</summary>
public class TrxResultParserTests
{
    private const string Trx = """
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="N.C.Passes" outcome="Passed" duration="00:00:01.2345678" />
    <UnitTestResult testName="N.C.NoDuration" outcome="Passed" />
    <UnitTestResult testName="N.C.BadDuration" outcome="Passed" duration="soon" />
    <UnitTestResult testName="N.C.Fails" outcome="Failed" duration="00:00:00.0420000">
      <Output>
        <ErrorInfo>
          <Message>Assert.Equal() Failure
Expected: 1</Message>
          <StackTrace>   at N.C.Fails() in C:\work\CTests.cs:line 42</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="N.C.Skips" outcome="NotExecuted" />
  </Results>
</TestRun>
""";

    private static CSharpTrxResult[] Parse(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, xml);
        try
        {
            var results = CSharpTrxResultParser.Parse(path, out var error);
            Assert.Null(error);
            return results.ToArray();
        }
        finally { try { File.Delete(path); } catch { /* 後始末の失敗は本題ではない */ } }
    }

    private static CSharpTrxResult Find(CSharpTrxResult[] results, string name)
        => results.Single(r => r.Name == name);

    [Fact]
    public void Reads_duration_as_a_timespan()
    {
        var r = Find(Parse(Trx), "N.C.Passes");
        Assert.Equal(CSharpTestStatus.Passed, r.Status);
        Assert.Equal(1.2345678, r.Duration!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void Missing_or_unparsable_duration_becomes_null()
    {
        var results = Parse(Trx);
        Assert.Null(Find(results, "N.C.NoDuration").Duration);
        Assert.Null(Find(results, "N.C.BadDuration").Duration);
    }

    [Fact]
    public void Failure_keeps_first_message_line_location_and_duration()
    {
        var r = Find(Parse(Trx), "N.C.Fails");
        Assert.Equal(CSharpTestStatus.Failed, r.Status);
        Assert.Equal("Assert.Equal() Failure", r.Message);
        Assert.Equal(@"C:\work\CTests.cs", r.SourcePath);
        Assert.Equal(42, r.Line);
        Assert.Equal(42, r.Duration!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void Not_executed_counts_as_skipped()
        => Assert.Equal(CSharpTestStatus.Skipped, Find(Parse(Trx), "N.C.Skips").Status);

    [Fact]
    public void Unreadable_file_reports_an_error_and_returns_nothing()
    {
        var results = CSharpTrxResultParser.Parse(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-missing.trx"), out var error);
        Assert.NotNull(error);
        Assert.Empty(results);
    }
}
