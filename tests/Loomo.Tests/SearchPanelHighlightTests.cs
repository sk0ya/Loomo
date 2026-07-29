using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 検索パネルが EditorSupport（Markdown プレビュー等）へ渡すハイライト条件。プレビューの一致は
/// 結果一覧に出てこないので、テキスト検索の間は結果を選んだかどうかに関わらずクエリを渡す。
/// </summary>
public sealed class SearchPanelHighlightTests
{
    private static SearchPanelViewModel CreateSut()
    {
        var mapper = new SearchResultTreeMapper();
        return new SearchPanelViewModel(new FakeWorkspaceService(),
            new SearchPanelQuery(new EmptySearchService(), mapper), mapper);
    }

    [Fact]
    public void テキスト検索ならクエリをそのまま渡す()
    {
        var sut = CreateSut();
        sut.Query = "loomo";

        Assert.Equal("loomo", sut.SupportHighlightTerm);
    }

    [Fact]
    public void 正規表現でも空にしない_ページ側は正規表現も塗れる()
    {
        var sut = CreateSut();
        sut.UseRegex = true;
        sut.Query = "loo.o";

        Assert.Equal("loo.o", sut.SupportHighlightTerm);   // Editor 用の HighlightTerm は空になる
        Assert.Equal("", sut.HighlightTerm);
        Assert.True(sut.HighlightUseRegex);
    }

    [Theory]
    [InlineData(SearchScope.FileName)]
    [InlineData(SearchScope.Terminal)]
    [InlineData(SearchScope.Class)]
    [InlineData(SearchScope.Symbol)]
    public void テキスト検索以外は塗らない(SearchScope scope)
    {
        var sut = CreateSut();
        sut.Query = "loomo";
        sut.Scope = scope;

        Assert.Equal("", sut.SupportHighlightTerm);
    }

    [Fact]
    public void 条件が変わるたびに通知する()
    {
        var sut = CreateSut();
        var raised = 0;
        sut.SupportHighlightChanged += (_, _) => raised++;

        sut.Query = "loomo";
        sut.CaseSensitive = true;
        sut.UseRegex = true;
        sut.Scope = SearchScope.FileName;

        Assert.Equal(4, raised);
    }

    private sealed class EmptySearchService : IWorkspaceSearchService
    {
        public Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string query, int max, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<FileSearchHit>>(Array.Empty<FileSearchHit>());

        public Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<ContentSearchHit>>(Array.Empty<ContentSearchHit>());
    }
}
