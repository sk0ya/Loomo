using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class EditorSupportControllerTests
{
    [Fact]
    public void Changing_source_records_history_and_returns_previous_source()
    {
        var controller = new EditorSupportController();
        var first = Tab("first.md");
        var second = Tab("second.md");

        Assert.True(controller.TryChangeSource(first, false, out var initial));
        Assert.Null(initial);
        Assert.True(controller.TryChangeSource(second, false, out var previous));
        Assert.Same(first, previous);
        Assert.Same(second, controller.Source);
        Assert.True(controller.History.CanGoBack);
    }

    [Fact]
    public void Pinned_source_rejects_automatic_change_but_allows_forced_change()
    {
        var controller = new EditorSupportController();
        var first = Tab("first.md");
        var second = Tab("second.md");
        controller.TryChangeSource(first, false, out _);
        controller.IsPinned = true;

        Assert.False(controller.TryChangeSource(second, false, out _));
        Assert.Same(first, controller.Source);
        Assert.True(controller.TryChangeSource(second, true, out _));
        Assert.Same(second, controller.Source);
    }

    [Fact]
    public void Navigation_change_does_not_append_to_history()
    {
        var controller = new EditorSupportController();
        var first = Tab("first.md");
        var second = Tab("second.md");
        controller.TryChangeSource(first, false, out _);
        controller.TryChangeSource(second, false, out _);

        Assert.Equal("first.md", controller.History.GoBack());
        controller.IsNavigating = true;
        controller.TryChangeSource(first, true, out _);

        Assert.False(controller.History.CanGoBack);
        Assert.True(controller.History.CanGoForward);
    }

    // ── ワークスペース切替 ─────────────────────────────────────────────
    //    エディタタブの実体はワークスペースごとに生き続け、戻ってくると同じインスタンスが再び追従元になる。
    //    だから「追従元を外す」だけでは足りず、その追従元に紐づいて覚えていたものを全部捨てる必要がある。

    [Fact]
    public void ワークスペース切替で追従元に紐づく状態を全部捨てる()
    {
        var controller = new EditorSupportController();
        var first = Tab(@"C:\a\First.cs");
        var second = Tab(@"C:\a\Second.cs");
        controller.TryChangeSource(first, false, out _);
        controller.TryChangeSource(second, false, out _);
        controller.IsPinned = true;
        controller.CommitOutline(new EditorSupportOutlineCommit(
            EditorSupportOutlineCommitKind.Replace, second, @"C:\a\Second.cs", [], null, (1, 0)));
        controller.ScheduleReadyRetry(TimeSpan.FromMinutes(5), (_, _) => { });
        controller.AdvanceReadyAttempt();

        var previous = controller.ResetForWorkspaceSwitch();

        Assert.Same(second, previous);            // 呼び元がイベント購読を外せるように返す
        Assert.Null(controller.Source);
        Assert.False(controller.IsPinned);
        Assert.False(controller.History.CanGoBack);   // 戻るで前のワークスペースのファイルを開かない
        Assert.False(controller.History.CanGoForward);
        Assert.Null(controller.OutlineRoots);
        Assert.Equal(0, controller.ReadyAttempts);    // 準備待ちの猶予を前のファイルから引き継がない
    }

    [Fact]
    public void 戻ってきた同じタブを前に出していた構造と取り違えない()
    {
        // 切替前に出していた構造が残ると、戻った直後のキャレット移動が「構造は出ている」と読んで
        // ②だけの差し替えへ進む。言語サーバーは切替で落とされているので、ツリーは古いまま動かない。
        var controller = new EditorSupportController();
        var tab = Tab(@"C:\a\Foo.cs");
        controller.TryChangeSource(tab, false, out _);
        controller.CommitOutline(new EditorSupportOutlineCommit(
            EditorSupportOutlineCommitKind.Replace, tab, @"C:\a\Foo.cs", [], null, (1, 0)));

        controller.ResetForWorkspaceSwitch();

        Assert.True(controller.TryChangeSource(tab, false, out _));
        Assert.False(controller.OutlineMatches(tab, @"C:\a\Foo.cs"));
    }

    [Fact]
    public async Task Unsupported_content_produces_stable_fallback_page()
    {
        var pipeline = new EditorSupportPipeline();

        var content = await pipeline.PrepareAsync(null, new EditorSupportContext(
            FilePath: null, Text: "", BaseFolder: "", ReadyPageKey: null,
            PreviewTheme: "dark"));

        Assert.Equal("Editor Support", content.Title);
        Assert.Contains("このファイルに対応するサポートはありません", content.Html);
        Assert.False(content.ShowOpenInBrowser);
        Assert.False(content.ShowExport);
    }

    private static EditorTab Tab(string path)
        => new(Guid.NewGuid()) { Pending = new EditorTabSnapshot { FilePath = path } };
}
