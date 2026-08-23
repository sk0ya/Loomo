using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Diff ペインの表示モード（テキスト差分／Markdown レンダリング差分・設計書 §24.10）の出し入れ。
/// 切り替えを出すのは Markdown のときだけ、というのがここの肝——押せるのに何も起きない項目を作らない（§24.7）。
/// </summary>
public class DiffMarkdownRenderModeTests
{
    /// <summary>比較モードは git に触らないので、実体のまま組み立ててよい（DiffComparisonTests と同じ）。</summary>
    private static DiffSessionViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var git = new GitService(workspace);
        var files = new DiffFileGateway();
        return new DiffSessionViewModel(git, new FakeEditorService(), workspace, files,
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
    }

    [Fact]
    public void 既定はテキスト差分()
    {
        var sut = CreateSut();

        Assert.False(sut.IsMarkdownRender);
        Assert.False(sut.IsMarkdownRenderActive);
        Assert.True(sut.ShowSideText);          // 既定の表示形式（左右並び）はそのまま
    }

    [Fact]
    public void Markdownならレンダリング表示へ切り替えられテキスト差分は退く()
    {
        var sut = CreateSut();
        sut.ShowComparison(new DiffComparison(
            "doc.md", "# 旧\n", "クリップボード", "# 新\n", @"C:\ws\doc.md"));

        Assert.True(sut.CanRenderMarkdown);

        sut.IsMarkdownRender = true;

        Assert.True(sut.IsMarkdownRenderActive);
        Assert.False(sut.ShowSideText);
        Assert.False(sut.ShowUnifiedText);
    }

    [Fact]
    public void Markdown以外では切り替えを出さずテキスト差分のままにする()
    {
        var sut = CreateSut();
        sut.IsMarkdownRender = true;    // 直前の .md で倒したまま持ち越したとする

        sut.ShowComparison(new DiffComparison(
            "Program.cs", "var a = 1;", "クリップボード", "var a = 2;", @"C:\ws\Program.cs"));

        Assert.False(sut.CanRenderMarkdown);      // ヘッダーの切り替え自体を出さない
        Assert.False(sut.IsMarkdownRenderActive); // 倒れたままでも効かない
        Assert.True(sut.ShowSideText);
    }

    [Fact]
    public void レンダリング表示中は左右と統合の切り替えを出さない()
    {
        var sut = CreateSut();
        sut.ShowComparison(new DiffComparison(
            "doc.md", "# 旧\n", "クリップボード", "# 新\n", @"C:\ws\doc.md"));

        Assert.True(sut.CanChooseTextLayout);   // テキスト差分では出す

        sut.IsMarkdownRender = true;

        // 押しても画面は変わらず読み直しだけが走るので、切り替えごと引っ込める
        Assert.False(sut.CanChooseTextLayout);
    }

    [Fact]
    public void 出どころのファイルが無い比較では切り替えを出さない()
    {
        var sut = CreateSut();

        sut.ShowComparison(new DiffComparison("クリップボード", "あ", "選択範囲", "い"));

        Assert.False(sut.CanRenderMarkdown);
    }
}
