using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>workspace edit の結果文。</summary>
public class WorkspaceEditOutcomeTests
{
    [Fact]
    public void 適用できたときは呼び出し側の成功文に任せる()
    {
        Assert.Null(WorkspaceEditOutcome.Ok().Describe("using整理"));
    }

    [Fact]
    public void 失敗は理由つきで出す()
    {
        var text = WorkspaceEditOutcome.Fail("ワークスペースが開かれていません。")
            .Describe("using整理");

        Assert.Equal("「using整理」を適用できませんでした: ワークスペースが開かれていません。", text);
    }
}
