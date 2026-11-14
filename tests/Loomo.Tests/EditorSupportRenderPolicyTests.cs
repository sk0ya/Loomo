using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// EditorSupport ペインの描画判定（<see cref="EditorSupportRenderPolicy"/>）の検証。
/// 自動表示はせず、ペインが実際に表示されている
/// （タイルで可視 / 袖ミニチュア）ときだけ描く。
/// 回帰の核心は「EditorSupport をミニチュアに出しているとき中身を描く」こと（これを欠くと袖が真っ黒になる）。
/// </summary>
public class EditorSupportRenderPolicyTests
{
    [Fact]
    public void ShouldRender_タイルで可視なら描く()
    {
        Assert.True(EditorSupportRenderPolicy.ShouldRender(
            paneVisibleInLayout: true,
            inThumbnail: false));
    }

    [Fact]
    public void ShouldRender_ミニチュアに出ていれば描く_回帰()
    {
        Assert.True(EditorSupportRenderPolicy.ShouldRender(
            paneVisibleInLayout: false,
            inThumbnail: true));
    }

    [Fact]
    public void ShouldRender_不可視なら描かない_自動表示しない()
    {
        Assert.False(EditorSupportRenderPolicy.ShouldRender(
            paneVisibleInLayout: false,
            inThumbnail: false));
    }
}
