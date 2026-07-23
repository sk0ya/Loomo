using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>node プロセス列挙（<see cref="TsNodeProcessEnumerator"/>）の inspect ポート推定と JSON パースのテスト。</summary>
public class TsNodeProcessEnumeratorTests
{
    [Theory]
    [InlineData("node app.js", null)]
    [InlineData("node --inspect app.js", 9229)]
    [InlineData("node --inspect-brk app.js", 9229)]
    [InlineData("node --inspect=9230 app.js", 9230)]
    [InlineData("node --inspect=127.0.0.1:9231 app.js", 9231)]
    [InlineData("node --inspect-brk=0.0.0.0:9232 server.js", 9232)]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" --inspect=9240 dist/main.js", 9240)]
    [InlineData("node --inspect-port=9250 app.js", null)]   // 対応外の別名は拾わない（誤検出しない）
    public void ParseInspectPort_covers_flag_variants(string commandLine, int? expected)
        => Assert.Equal(expected, TsNodeProcessEnumerator.ParseInspectPort(commandLine));

    [Fact]
    public void Parse_handles_single_object_and_array_and_sorts_inspectable_first()
    {
        var single = TsNodeProcessEnumerator.Parse(
            """{"ProcessId":100,"CommandLine":"node --inspect app.js"}""");
        Assert.Single(single);
        Assert.Equal(9229, single[0].InspectPort);

        var multi = TsNodeProcessEnumerator.Parse("""
            [
              {"ProcessId":200,"CommandLine":"node plain.js"},
              {"ProcessId":100,"CommandLine":"node --inspect=9230 app.js"}
            ]
            """);
        Assert.Equal(2, multi.Count);
        Assert.Equal(100, multi[0].Pid);      // inspect 付きが先
        Assert.True(multi[0].CanAttach);
        Assert.False(multi[1].CanAttach);
    }

    [Fact]
    public void Parse_tolerates_banner_noise_and_non_json()
    {
        Assert.Empty(TsNodeProcessEnumerator.Parse(""));
        Assert.Empty(TsNodeProcessEnumerator.Parse("Get-CimInstance : failed"));
        var withNoise = TsNodeProcessEnumerator.Parse(
            "some banner\n{\"ProcessId\":1,\"CommandLine\":\"node a.js\"}");
        Assert.Single(withNoise);
    }
}
