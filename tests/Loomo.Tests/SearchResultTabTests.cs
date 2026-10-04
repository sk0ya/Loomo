using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 検索結果をタブとして残す（VS Code の Search Editor 相当・§23.3.1）。
/// 残したタブは「残した時点の写し」で、新しい検索をしても消えず、ワークスペース状態に載って再起動後も戻る。
/// タブは検索ペイン内のタブ帯（と ▾ 一覧）で切り替え、サイドバーの TABS には載せない。
/// </summary>
public sealed class SearchResultTabTests : IDisposable
{
    private const string Root = @"C:\work\app";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"loomo-searchtab-{Guid.NewGuid():N}");

    public SearchResultTabTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SearchPanelViewModel CreateSut(FakeWorkspaceService? workspace = null)
    {
        var mapper = new SearchResultTreeMapper();
        return new SearchPanelViewModel(workspace ?? new FakeWorkspaceService(Root),
            new SearchPanelQuery(new NoSearch(), mapper), mapper);
    }

    /// <summary>クエリを入れて、走り出した（遅延付きの）検索を止めてから、結果を直接並べる
    /// ——本物の検索が後から結果を空で上書きしないように。</summary>
    private static void ShowLiveResults(SearchPanelViewModel sut, string query, params ContentSearchHit[] hits)
    {
        sut.Query = query;
        sut.CancelSearchCommand.Execute(null);
        sut.Results.Clear();
        var groups = hits
            .GroupBy(h => h.FullPath)
            .Select(g => new SearchFileGroup(g.Key, g.First().RelativePath, g.Select(h => new SearchMatchItem(h))))
            .ToList();
        foreach (var node in new SearchResultTreeMapper().Map(groups))
            sut.Results.Add(node);
    }

    private static ContentSearchHit Hit(string relative, int line, string text, int column = 1)
        => new(Path.Combine(Root, relative.Replace('/', '\\')), relative, line, column, text);

    private static IEnumerable<SearchMatchItem> AllMatches(IEnumerable<object> roots)
    {
        foreach (var node in roots)
        {
            switch (node)
            {
                case SearchFolderNode folder:
                    foreach (var m in AllMatches(folder.Children)) yield return m;
                    break;
                case SearchFileGroup group:
                    foreach (var m in group.Matches) yield return m;
                    break;
            }
        }
    }

    // ===== タブに残す =====

    [Fact]
    public void 残したタブは新しい検索をしても消えず_切り替えると残した結果が出る()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("src/a.cs", 3, "var foo = 1;"), Hit("src/b.cs", 7, "foo();"));

        var tab = sut.PinResults();

        Assert.NotNull(tab);
        Assert.Single(sut.PinnedTabs);
        Assert.Null(sut.ActiveTab);   // 残した後も現在の検索のまま（続けて検索し直すため）
        Assert.Equal("テキスト「foo」", tab!.Title);
        Assert.Equal(2, tab.MatchCount);

        ShowLiveResults(sut, "bar", Hit("src/c.cs", 1, "bar"));
        sut.ShowTab(tab.Id);

        Assert.True(sut.IsViewingTab);
        var shown = AllMatches(sut.DisplayedResults).Select(m => (m.Line, m.Preview)).ToList();
        Assert.Equal([(3, "var foo = 1;"), (7, "foo();")], shown);
        // ハイライトもタブの語で塗る（いま入力欄にある bar ではない）。
        Assert.Equal("foo", sut.HighlightQuery);
        Assert.Equal("foo", sut.HighlightTerm);

        sut.ShowLive();
        Assert.Equal("bar", Assert.Single(AllMatches(sut.DisplayedResults)).Preview);
    }

    [Fact]
    public void 残した結果は写しなので置換させない()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("src/a.cs", 3, "foo"));
        var tab = sut.PinResults()!;
        sut.IsReplaceVisible = true;

        sut.ShowTab(tab.Id);

        Assert.False(sut.CanReplace);
        Assert.False(sut.IsReplaceVisible);
        Assert.False(sut.ReplaceOne(AllMatches(sut.DisplayedResults).First()));
        Assert.Equal((0, 0), sut.ReplaceAll());
    }

    [Fact]
    public void ターミナル内の一致はタブに残さない()
    {
        var sut = CreateSut();
        sut.Scope = SearchScope.Terminal;
        sut.Results.Add(new SearchFileGroup("", "ターミナル",
            [SearchMatchItem.ForTerminal(new TerminalSearchHit(0, 0, 3, "foo"))]));

        Assert.False(sut.CanPinResults);
        Assert.Null(sut.PinResults());
    }

    [Fact]
    public void 見ていたタブを閉じると現在の検索へ戻る()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("a.cs", 1, "foo"));
        var tab = sut.PinResults()!;
        sut.ShowTab(tab.Id);
        var changes = 0;
        sut.TabsChanged += (_, _) => changes++;

        sut.CloseTab(tab.Id);

        Assert.Empty(sut.PinnedTabs);
        Assert.Null(sut.ActiveTab);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void 検索の種類を切り替えると現在の検索へ戻る()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("a.cs", 1, "foo"));
        sut.ShowTab(sut.PinResults()!.Id);

        sut.Scope = SearchScope.FileName;

        Assert.False(sut.IsViewingTab);
        Assert.Single(sut.PinnedTabs);
    }

    // ===== 復元の完全性 =====

    [Fact]
    public void 残したタブと見ていたタブは保存して戻る()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("src/a.cs", 3, "  var foo = 1;", column: 7));
        var first = sut.PinResults()!;
        ShowLiveResults(sut, "bar", Hit("src/b.cs", 9, "bar();"));
        var second = sut.PinResults()!;
        sut.ShowTab(second.Id);

        var snapshots = sut.CaptureTabs();

        var restored = CreateSut();
        var changes = 0;
        restored.TabsChanged += (_, _) => changes++;
        restored.RestoreTabs(snapshots);

        Assert.Equal(0, changes);   // 戻しただけでは保存し直さない
        Assert.Equal([first.Id, second.Id], restored.PinnedTabs.Select(t => t.Id));
        Assert.Equal(second.Id, restored.ActiveTab?.Id);
        restored.ShowTab(first.Id);
        var match = Assert.Single(AllMatches(restored.DisplayedResults));
        Assert.Equal((3, 7, "var foo = 1;"), (match.Line, match.Column, match.Preview));
        Assert.Equal(Path.Combine(Root, @"src\a.cs"), match.FullPath);
    }

    [Fact]
    public void 残したタブはworkspaces_jsonを往復する()
    {
        var sut = CreateSut();
        sut.UseRegex = true;
        ShowLiveResults(sut, "fo+", Hit("src/a.cs", 3, "foo"));
        sut.ShowTab(sut.PinResults()!.Id);
        var workspace = new WorkspaceSnapshot { RootPath = Root, Name = "app", SearchTabs = sut.CaptureTabs() };
        var path = Path.Combine(_dir, "workspaces.json");

        using (var store = new WorkspaceStateStore(path))
            store.Save(new WorkspaceState { Workspaces = [workspace], ActiveWorkspaceId = workspace.Id });

        using var reload = new WorkspaceStateStore(path);
        var loaded = reload.Load().Workspaces.Single();
        var tab = Assert.Single(loaded.SearchTabs);
        Assert.Equal("Text", tab.Scope);
        Assert.True(tab.UseRegex);
        Assert.True(tab.IsActive);
        Assert.Equal((3, "foo"), (tab.Hits.Single().Line, tab.Hits.Single().Text));
    }

    [Fact]
    public void 旧データや壊れた保存は空のタブとして捨てる()
    {
        var sut = CreateSut();

        sut.RestoreTabs([new SearchTabSnapshot { Query = "x", Scope = "存在しない" }, null!]);

        Assert.Empty(sut.PinnedTabs);
        sut.RestoreTabs(null);
        Assert.Empty(sut.PinnedTabs);
    }

    [Fact]
    public void マルチルートでは表示パスにフォルダー名を付ける()
    {
        var workspace = new FakeWorkspaceService(Root);
        workspace.AddFolder(@"C:\work\lib");
        var sut = CreateSut(workspace);
        var libHit = new ContentSearchHit(@"C:\work\lib\x.cs", "lib/x.cs", 2, 1, "foo");
        ShowLiveResults(sut, "foo", Hit("a.cs", 1, "foo"), libHit);
        sut.ShowTab(sut.PinResults()!.Id);

        var paths = AllMatches(sut.DisplayedResults).Select(m => m.RelativePath.Replace('\\', '/')).ToList();

        Assert.Contains("app/a.cs", paths);
        Assert.Contains("lib/x.cs", paths);
    }

    [Fact]
    public void ファイル名検索の結果はファイルだけのタブになる()
    {
        var sut = CreateSut();
        sut.Scope = SearchScope.FileName;
        sut.Query = "read";
        sut.CancelSearchCommand.Execute(null);
        sut.Results.Clear();
        var group = new SearchFileGroup(Path.Combine(Root, "README.md"), "README.md", []);
        sut.Results.Add(group);
        var tab = sut.PinResults()!;

        Assert.Equal("ファイル「read」", tab.Title);
        Assert.Equal(0, tab.MatchCount);
        Assert.Equal(1, tab.FileCount);
    }

    // ===== 検索ペイン内のタブ帯 =====

    [Fact]
    public void タブ帯は現在の検索と残したタブを並べ_残すまでは出さない()
    {
        var sut = CreateSut();
        Assert.False(sut.HasTabStrip);   // 「現在の検索」1枚だけの帯のために場所を取らない
        Assert.Equal([(Guid?)null], sut.TabStrip.Select(e => e.TabId));

        ShowLiveResults(sut, "foo", Hit("a.cs", 1, "foo"));
        var first = sut.PinResults()!;
        ShowLiveResults(sut, "bar", Hit("b.cs", 1, "bar"));
        var second = sut.PinResults()!;

        Assert.True(sut.HasTabStrip);
        Assert.Equal(["現在の検索", "テキスト「foo」", "テキスト「bar」"], sut.TabStrip.Select(e => e.Title));
        Assert.Equal([false, true, true], sut.TabStrip.Select(e => e.CanClose));
        Assert.Equal([true, false, false], sut.TabStrip.Select(e => e.IsActive));

        sut.SelectTabStripEntry(sut.TabStrip[2]);
        Assert.Equal(second.Id, sut.ActiveTab?.Id);
        Assert.Equal([false, false, true], sut.TabStrip.Select(e => e.IsActive));

        sut.CloseTabStripEntryCommand.Execute(sut.TabStrip[2]);
        Assert.Null(sut.ActiveTab);
        Assert.Equal([(Guid?)null, first.Id], sut.TabStrip.Select(e => e.TabId));
        Assert.True(sut.TabStrip[0].IsActive);

        sut.SelectTabStripEntry(sut.TabStrip[1]);
        sut.SelectTabStripEntry(sut.TabStrip[0]);   // 「現在の検索」へ戻る
        Assert.False(sut.IsViewingTab);

        sut.CloseTabStripEntryCommand.Execute(sut.TabStrip[0]);   // 「現在の検索」は閉じない
        Assert.Single(sut.PinnedTabs);

        sut.RestoreTabs([]);
        Assert.False(sut.HasTabStrip);
    }

    [Fact]
    public void 復元した見ていたタブはタブ帯でも選ばれている()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("a.cs", 1, "foo"));
        sut.ShowTab(sut.PinResults()!.Id);

        var restored = CreateSut();
        restored.RestoreTabs(sut.CaptureTabs());

        Assert.Equal([false, true], restored.TabStrip.Select(e => e.IsActive));
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
