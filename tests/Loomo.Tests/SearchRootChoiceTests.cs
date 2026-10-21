using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 検索フォルダー欄の ▾：ワークスペース全体／各ワークスペースフォルダー／ピン留めフォルダーを
/// 打たずに選べる。選んだ値は手入力と同じ SearchRoot の表記になる。
/// </summary>
public sealed class SearchRootChoiceTests
{
    private const string Root = @"C:\work\app";

    private static SearchPanelViewModel CreateSut(FakeWorkspaceService workspace, params string[] pins)
    {
        var mapper = new SearchResultTreeMapper();
        return new SearchPanelViewModel(workspace, new SearchPanelQuery(new NoSearch(), mapper), mapper,
            new FakePins(pins));
    }

    [Fact]
    public void 単一フォルダーでは全体とピンが並び選ぶと相対パスになる()
    {
        var sut = CreateSut(new FakeWorkspaceService(Root), @"C:\work\app\src\Core", @"C:\work\app\docs");

        var choices = sut.RootChoices();

        Assert.Equal(new[] { "ワークスペース全体", "Core", "docs" }, choices.Select(c => c.Label));
        Assert.Equal(new[] { false, true, true }, choices.Select(c => c.IsPin));
        Assert.True(choices[0].IsCurrent);

        sut.SelectRootChoice(choices[1]);
        Assert.Equal("src/Core", sut.SearchRoot);
        Assert.True(sut.RootChoices()[1].IsCurrent);

        sut.SelectRootChoice(sut.RootChoices()[0]);
        Assert.Equal("", sut.SearchRoot);
    }

    [Fact]
    public void マルチルートでは各フォルダーも並びピンはフォルダー名付きの表記になる()
    {
        var workspace = new FakeWorkspaceService(Root);
        workspace.AddFolder(@"D:\shared\lib");
        var sut = CreateSut(workspace, @"D:\shared\lib\tools");

        var choices = sut.RootChoices();

        Assert.Equal(new[] { "ワークスペース全体", "app", "lib", "tools" }, choices.Select(c => c.Label));
        sut.SelectRootChoice(choices[3]);
        Assert.Equal("lib/tools", sut.SearchRoot);
    }

    [Fact]
    public void ピンが無ければ全体だけ()
    {
        var sut = CreateSut(new FakeWorkspaceService(Root));

        Assert.Single(sut.RootChoices());
    }

    private sealed class FakePins(IReadOnlyList<string> pins) : IFolderPinStore
    {
        public IReadOnlyList<string> AllPins => pins;
        public bool IsPinned(string fullPath) => pins.Contains(fullPath);
        public bool CanPin(string fullPath) => false;
        public void Pin(string fullPath) { }
        public void Unpin(string fullPath) { }
        public event EventHandler? PinsChanged { add { } remove { } }
    }

    private sealed class NoSearch : IWorkspaceSearchService
    {
        public Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string query, int max, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<FileSearchHit>>(Array.Empty<FileSearchHit>());
        public Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<ContentSearchHit>>(Array.Empty<ContentSearchHit>());
        public Task<IReadOnlyList<AdvancedFileSearchHit>> SearchFilesAsync(AdvancedSearchOptions options, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<AdvancedFileSearchHit>>(Array.Empty<AdvancedFileSearchHit>());
    }
}
