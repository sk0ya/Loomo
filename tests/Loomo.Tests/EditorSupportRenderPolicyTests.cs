using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// EditorSupport ペインの描画判定（<see cref="EditorSupportRenderPolicy"/>）の検証。
/// 自動表示はせず、ペインが実際に表示されている
/// （タイルで可視 / ソロで舞台 / 袖・俯瞰ミニチュア）ときだけ描く。
/// 回帰の核心は「ソロモードで EditorSupport を舞台やミニチュアに出しているとき中身を描く」こと
/// （これを欠くと舞台や袖が真っ黒になる）。
/// </summary>
public class EditorSupportRenderPolicyTests
{
    [Fact]
    public void ShouldRender_舞台に立っていれば描く_回帰()
    {
        // ソロモードで EditorSupport が舞台：タイルでは不可視でも描画する。
        Assert.True(EditorSupportRenderPolicy.ShouldRender(
            onStage: true,
            paneVisibleInLayout: false,
            inThumbnail: false));
    }

    [Fact]
    public void ShouldRender_タイルで可視なら描く()
    {
        Assert.True(EditorSupportRenderPolicy.ShouldRender(
            onStage: false,
            paneVisibleInLayout: true,
            inThumbnail: false));
    }

    [Fact]
    public void ShouldRender_ミニチュアに出ていれば描く_回帰()
    {
        Assert.True(EditorSupportRenderPolicy.ShouldRender(
            onStage: false,
            paneVisibleInLayout: false,
            inThumbnail: true));
    }

    [Fact]
    public void ShouldRender_舞台でもなく不可視なら描かない_自動表示しない()
    {
        Assert.False(EditorSupportRenderPolicy.ShouldRender(
            onStage: false,
            paneVisibleInLayout: false,
            inThumbnail: false));
    }
}
