using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 検索結果をタブとして残す（VS Code の Search Editor 相当・§23.3.1）と、検索結果をペグボードへ送る（§23.3）。
/// 残したタブは「残した時点の写し」で、新しい検索をしても消えず、ワークスペース状態に載って再起動後も戻る。
/// タブはエディタ等と同じ仕組み（TabsViewModel＝TABS と ▾ 一覧）に乗る。
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
        Assert.True(sut.HasLiveResults);   // ペグボードへは送れる
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
        var tab = sut.PinResults()!;

        var text = tab.ToPegboardText(workspace);

        Assert.Contains("app/a.cs:1: foo", text);
        Assert.Contains("lib/x.cs:2: foo", text);
    }

    // ===== ペグボードへ =====

    [Fact]
    public void 結果全体は検索語とpath_line_行の一覧で1枚になる()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("src/a.cs", 3, "    var foo = 1;"), Hit("src/b.cs", 7, "foo();"));
        sut.StatusMessage = "2 件 / 2 ファイル";
        SearchPegboardPayload? sent = null;
        sut.PegboardSendRequested += (_, p) => sent = p;

        sut.SendResultsToPegboard();

        Assert.NotNull(sent);
        Assert.Equal("検索「foo」（2 件 / 2 ファイル）", sent!.Title);
        var lines = sent.Content.TrimEnd('\n').Split('\n');
        Assert.Equal(["検索「foo」  2 件 / 2 ファイル", "src/a.cs:3: var foo = 1;", "src/b.cs:7: foo();"], lines);
    }

    [Fact]
    public void 一致行1件はその行だけを送る()
    {
        var sut = CreateSut();
        ShowLiveResults(sut, "foo", Hit("src/a.cs", 3, "foo();"));
        SearchPegboardPayload? sent = null;
        sut.PegboardSendRequested += (_, p) => sent = p;

        sut.SendMatchToPegboard(AllMatches(sut.Results).Single());

        Assert.Equal(new SearchPegboardPayload("src/a.cs:3: foo();", "src/a.cs:3"), sent);
    }

    [Fact]
    public void ファイル名検索のヒットはパスだけの行になる()
    {
        var sut = CreateSut();
        sut.Scope = SearchScope.FileName;
        sut.Query = "read";
        sut.CancelSearchCommand.Execute(null);
        sut.Results.Clear();
        var group = new SearchFileGroup(Path.Combine(Root, "README.md"), "README.md", []);
        sut.Results.Add(group);
        SearchPegboardPayload? sent = null;
        sut.PegboardSendRequested += (_, p) => sent = p;

        sut.SendGroupToPegboard(group);
        var tab = sut.PinResults()!;

        Assert.EndsWith("\nREADME.md\n", sent!.Content);
        Assert.Equal("ファイル「read」", tab.Title);
        Assert.Equal(0, tab.MatchCount);
        Assert.Equal(1, tab.FileCount);
    }

    // ===== タブの仕組み（TABS・▾）とペグボードへの配線 =====

    [Fact]
    public void 残したタブはTABSの検索の群に並び_見ているものが選ばれる()
    {
        var sut = CreateSut();
        var tabs = new TabsViewModel(new TabIconService());
        var pegboard = new PegboardViewModel();
        SearchPanelLinks.Connect(sut, tabs, pegboard);

        ShowLiveResults(sut, "foo", Hit("a.cs", 1, "foo"));
        var first = sut.PinResults()!;
        ShowLiveResults(sut, "bar", Hit("b.cs", 1, "bar"));
        var second = sut.PinResults()!;

        Assert.Equal([first.Id, second.Id], tabs.SearchTabs.Select(t => t.Id));
        Assert.All(tabs.SearchTabs, t => Assert.Equal(TabEntryKind.Search, t.Kind));
        Assert.All(tabs.SearchTabs, t => Assert.False(t.CanDetach));
        Assert.Null(tabs.ActiveSearchTab);
        Assert.Equal(2, tabs.Kinds.Single(k => k.Kind == TabEntryKind.Search).Count);

        sut.ShowTab(second.Id);
        Assert.Equal(second.Id, tabs.ActiveSearchTab?.Id);
        Assert.Equal("テキスト「bar」", tabs.ActiveSearchTab?.Title);

        sut.CloseTab(second.Id);
        Assert.Equal([first.Id], tabs.SearchTabs.Select(t => t.Id));
        Assert.Null(tabs.ActiveSearchTab);

        sut.RestoreTabs([]);
        Assert.Empty(tabs.SearchTabs);
    }

    [Fact]
    public void ペグボードへ送るとテキストの項目になる()
    {
        var sut = CreateSut();
        var pegboard = new PegboardViewModel();
        SearchPanelLinks.Connect(sut, new TabsViewModel(new TabIconService()), pegboard);
        ShowLiveResults(sut, "foo", Hit("a.cs", 4, "foo"));

        sut.SendResultsToPegboard();

        var item = Assert.Single(pegboard.Items);
        Assert.Equal("text", item.Type);
        Assert.Contains("a.cs:4: foo", item.Content);
    }

    [Fact]
    public void TABSの他を閉じる_すべて閉じるは検索のタブにも効く()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var plan = WorkspaceTabClosePolicy.CreatePlan(TabEntryKind.Search, ids[1], WorkspaceTabCloseScope.Others,
            [], [], [], ids);

        Assert.Equal(TabEntryKind.Search, plan.Kind);
        Assert.Equal([ids[0], ids[2]], plan.TabIds);
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
