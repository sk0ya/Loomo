using sk0ya.Loomo.Core.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>TS 実行対象の <c>npm:スクリプト名</c> エンコード（<see cref="TsLaunchTarget"/>）のテスト。</summary>
public class TsLaunchTargetTests
{
    [Fact]
    public void Format_and_parse_roundtrip()
    {
        var stored = TsLaunchTarget.FormatNpmScript("dev");
        Assert.Equal("npm:dev", stored);
        Assert.True(TsLaunchTarget.TryParseNpmScript(stored, out var script));
        Assert.Equal("dev", script);
    }

    [Theory]
    [InlineData(@"src\index.ts")]
    [InlineData(@"C:\work\app\src\index.ts")]
    [InlineData("")]
    public void File_paths_are_not_npm_scripts(string target)
    {
        Assert.False(TsLaunchTarget.TryParseNpmScript(target, out var script));
        Assert.Equal("", script);
    }

    [Fact]
    public void Parse_trims_script_name()
    {
        Assert.True(TsLaunchTarget.TryParseNpmScript("npm: dev ", out var script));
        Assert.Equal("dev", script);
    }

    [Fact]
    public void Empty_script_name_parses_as_npm_with_empty_name()
    {
        // 「npm モードだが名前未入力」の状態を表せる（起動時に検証で弾く）。
        Assert.True(TsLaunchTarget.TryParseNpmScript("npm:", out var script));
        Assert.Equal("", script);
    }
}
