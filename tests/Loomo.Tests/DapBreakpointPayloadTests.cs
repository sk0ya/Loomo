using System.Text.Json;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>setBreakpoints の payload 組み立て（<see cref="DapBreakpointPayload"/>）のテスト。
/// 任意項目を <c>null</c> で書くと netcoredbg が
/// <c>type must be string, but is null</c> でリクエストごと拒否し、そのソースのブレークポイントが
/// 1 件も張れなくなる（＝置いても止まらない）ため、null が出ないことを固定する。</summary>
public sealed class DapBreakpointPayloadTests
{
    private static string Json(params DebugBreakpoint[] bps) =>
        JsonSerializer.Serialize(DapBreakpointPayload.Build(bps, null));

    [Fact]
    public void 条件なしブレークポイントはlineだけを送る()
    {
        Assert.Equal("""[{"line":10}]""", Json(new DebugBreakpoint(10)));
    }

    [Fact]
    public void 空文字や空白の条件はキーごと省略する()
    {
        var json = Json(new DebugBreakpoint(3, Condition: "  ", HitCondition: "", LogMessage: null));

        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("condition", json);
        Assert.DoesNotContain("hitCondition", json);
        Assert.DoesNotContain("logMessage", json);
    }

    [Fact]
    public void 指定された条件だけを載せる()
    {
        var json = Json(new DebugBreakpoint(7, Condition: "i == 3", LogMessage: "hit"));

        Assert.Contains("""{"line":7,"condition":"i == 3","logMessage":"hit"}""", json);
        Assert.DoesNotContain("hitCondition", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void 無効な行は送らない()
    {
        var json = Json(new DebugBreakpoint(1, Enabled: false), new DebugBreakpoint(2));

        Assert.Equal("""[{"line":2}]""", json);
    }

    [Fact]
    public void 一時行は条件なしで足し永続行と重複させない()
    {
        var payload = DapBreakpointPayload.Build(
            new[] { new DebugBreakpoint(5, Condition: "x == 1") },
            new[] { 5, 9 });

        var json = JsonSerializer.Serialize(payload);

        Assert.Equal("""[{"line":5,"condition":"x == 1"},{"line":9}]""", json);
    }
}
