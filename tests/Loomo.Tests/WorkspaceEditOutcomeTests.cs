using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>workspace edit の結果文。<b>取り消しは失敗ではない</b>——編集プレビューで「キャンセル」を
/// 押しただけなのに「適用できませんでした: …」と出していた（2026-09 の修正）。</summary>
public class WorkspaceEditOutcomeTests
{
    [Fact]
    public void 適用できたときは呼び出し側の成功文に任せる()
    {
        Assert.Null(WorkspaceEditOutcome.Ok().Describe("using整理"));
    }

    [Fact]
    public void 取り消しは失敗として出さない()
    {
        var text = WorkspaceEditOutcome.Cancel().Describe("using整理");

        Assert.Equal("「using整理」は取り消しました。", text);
        Assert.DoesNotContain("できませんでした", text);
    }

    [Fact]
    public void 失敗は理由つきで出す()
    {
        var text = WorkspaceEditOutcome.Fail("ワークスペースが開かれていません。")
            .Describe("using整理");

        Assert.Equal("「using整理」を適用できませんでした: ワークスペースが開かれていません。", text);
    }
}
