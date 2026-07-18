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

    [Fact]
    public async Task Unsupported_content_produces_stable_fallback_page()
    {
        var pipeline = new EditorSupportPipeline();

        var content = await pipeline.PrepareAsync(null, new EditorSupportContext(
            FilePath: null, Text: "", WorkspaceRoot: "", ReadyPageKey: null,
            PreviewTheme: "dark"));

        Assert.Equal("Editor Support", content.Title);
        Assert.Contains("このファイルに対応するサポートはありません", content.Html);
        Assert.False(content.ShowOpenInBrowser);
        Assert.False(content.ShowExport);
    }

    [Fact]
    public void Only_latest_render_sequence_is_current()
    {
        var controller = new EditorSupportController();

        var first = controller.BeginRender();
        var second = controller.BeginRender();

        Assert.False(controller.IsLatestRender(first));
        Assert.True(controller.IsLatestRender(second));
    }

    private static EditorTab Tab(string path)
        => new(Guid.NewGuid()) { Pending = new EditorTabSnapshot { FilePath = path } };
}
