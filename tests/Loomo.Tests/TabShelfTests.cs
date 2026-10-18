using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>タブの棚（§24.29）。種類ごとの上限（ピン留めは数えない）を超えたら長く見ていないものから下げ、
/// 棚の行は 7 日で消える（ピン留めは残る）——という判定と、棚そのものの出し入れを見る。</summary>
public sealed class TabShelfTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private static TabShelfCandidate Tab(int minutesAgo, bool isProtected = false, bool pinned = false, Guid? id = null)
        => new(id ?? Guid.NewGuid(), T0.AddMinutes(-minutesAgo), isProtected, pinned);

    [Fact]
    public void 上限以内なら何も下げない()
        => Assert.Empty(TabShelfPolicy.SelectOverflow([Tab(5), Tab(10)], limit: 2));

    [Fact]
    public void 超えた分だけいちばん長く見ていないものから下げる()
    {
        var oldest = Tab(300);
        var older = Tab(200);
        var recent = Tab(1);

        var selected = TabShelfPolicy.SelectOverflow([recent, oldest, Tab(50), older], limit: 2);

        Assert.Equal([oldest.Id, older.Id], selected);
    }

    [Fact]
    public void ピン留めは上限に数えず下げもしない()
    {
        // ピン留め 3 枚 + 普通 2 枚、上限 2 → 普通のタブだけで数えるので超えていない
        var tabs = new[] { Tab(900, pinned: true), Tab(800, pinned: true), Tab(700, pinned: true), Tab(10), Tab(5) };

        Assert.Empty(TabShelfPolicy.SelectOverflow(tabs, limit: 2));
    }

    [Fact]
    public void ピン留めが何枚あっても普通のタブが超えた分だけ下げる()
    {
        var oldPlain = Tab(100);
        var tabs = new[] { Tab(999, pinned: true), Tab(998, pinned: true), oldPlain, Tab(10), Tab(5) };

        Assert.Equal([oldPlain.Id], TabShelfPolicy.SelectOverflow(tabs, limit: 2));
    }

    [Fact]
    public void 守られたタブは数には入るが下げない()
    {
        var protectedOld = Tab(500, isProtected: true);
        var plain = Tab(100);

        var selected = TabShelfPolicy.SelectOverflow([protectedOld, plain, Tab(1, isProtected: true)], limit: 1);

        Assert.Equal([plain.Id], selected);   // 2 枚超えているが、下げられるのは 1 枚だけ
    }

    [Fact]
    public void 見た時刻が同じなら先に開いたものから下げる()
    {
        var first = new TabShelfCandidate(Guid.NewGuid(), default, false);
        var second = new TabShelfCandidate(Guid.NewGuid(), default, false);

        Assert.Equal([first.Id], TabShelfPolicy.SelectOverflow([first, second], limit: 1));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-3, 10)]
    [InlineData(100000, 10)]
    [InlineData(15, 15)]
    public void 範囲外の設定値は既定の10枚に戻す(int value, int expected)
        => Assert.Equal(expected, TabShelfPolicy.Limit(value));

    [Fact]
    public void 棚の行は7日で消えピン留めは残る()
    {
        var old = Editor(@"C:\work\old.cs", shelvedDaysAgo: 7);
        var pinnedOld = Editor(@"C:\work\keep.cs", shelvedDaysAgo: 30, pinned: true);
        var fresh = Editor(@"C:\work\new.cs", shelvedDaysAgo: 6.9);

        Assert.Equal([old.Id], TabShelfPolicy.Expired([old, pinnedOld, fresh], T0));
        Assert.Null(TabShelfPolicy.DaysLeft(pinnedOld, T0));
        Assert.Equal(1, TabShelfPolicy.DaysLeft(fresh, T0));
    }

    [Fact]
    public void 同じファイルを何度下げても棚には新しい1行だけでピン留めは引き継ぐ()
    {
        var older = Editor(@"C:\work\a.cs", pinned: true);
        var newer = Editor(@"C:\WORK\a.cs");

        var merged = TabShelfPolicy.Merge([older, Editor(@"C:\work\b.cs")], [newer]);

        Assert.Equal(2, merged.Count);
        Assert.Same(newer, merged[0]);
        Assert.True(merged[0].Pinned);
    }

    [Fact]
    public void 棚は上限を超えたらピン留めしていない古い分を捨てる()
    {
        var shelf = Enumerable.Range(0, TabShelfPolicy.Capacity)
            .Select(i => Editor($@"C:\work\f{i}.cs", pinned: i == TabShelfPolicy.Capacity - 1))
            .ToList();

        var merged = TabShelfPolicy.Merge(shelf, [Editor(@"C:\work\new.cs")]);

        Assert.Equal(TabShelfPolicy.Capacity, merged.Count);
        Assert.Equal(@"C:\work\new.cs", merged[0].FilePath);
        Assert.Contains(merged, s => s.FilePath == $@"C:\work\f{TabShelfPolicy.Capacity - 1}.cs");   // ピン留めは残る
        Assert.DoesNotContain(merged, s => s.FilePath == $@"C:\work\f{TabShelfPolicy.Capacity - 2}.cs");
    }

    [Fact]
    public void エディタとブラウザは同じものとみなさない()
    {
        var editor = Editor(@"C:\work\index.html");
        var browser = new ShelvedTabSnapshot { Kind = ShelvedTabSnapshot.BrowserKind, Url = "https://example.com/" };

        Assert.False(TabShelfPolicy.IsSameTarget(editor, browser));
        Assert.True(TabShelfPolicy.IsSameTarget(browser,
            new ShelvedTabSnapshot { Kind = ShelvedTabSnapshot.BrowserKind, Url = "https://example.com/" }));
    }

    [Fact]
    public void 開き直したファイルの行は棚から外れる()
    {
        var shelf = new TabShelfViewModel();
        shelf.Load([Editor(@"C:\work\a.cs"), Editor(@"C:\work\b.cs")]);
        var changed = 0;
        shelf.Changed += (_, _) => changed++;

        shelf.ForgetFile(@"c:\work\A.cs");

        Assert.Equal([@"C:\work\b.cs"], shelf.Items.Select(i => i.Snapshot.FilePath));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void 読み込みでは保存の合図を出さない()
    {
        var shelf = new TabShelfViewModel();
        var changed = 0;
        shelf.Changed += (_, _) => changed++;

        shelf.Load([Editor(@"C:\work\a.cs")]);

        Assert.Equal(0, changed);
        Assert.True(shelf.HasItems);
    }

    [Fact]
    public void 期限切れを落とすとピン留め以外が消える()
    {
        var shelf = new TabShelfViewModel();
        shelf.Load([Editor(@"C:\work\old.cs", shelvedDaysAgo: 8), Editor(@"C:\work\keep.cs", shelvedDaysAgo: 8, pinned: true)]);

        Assert.True(shelf.PurgeExpired(T0));

        Assert.Equal([@"C:\work\keep.cs"], shelf.Items.Select(i => i.Snapshot.FilePath));
        Assert.False(shelf.PurgeExpired(T0));
    }

    [Fact]
    public void 行のピン留めを切り替えると保存の合図を出す()
    {
        var shelf = new TabShelfViewModel();
        shelf.Load([Editor(@"C:\work\a.cs")]);
        var changed = 0;
        shelf.Changed += (_, _) => changed++;

        shelf.TogglePinCommand.Execute(shelf.Items[0]);

        Assert.True(shelf.Items[0].IsPinned);
        Assert.True(shelf.Items[0].Snapshot.Pinned);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void 棚を空にしてもピン留めは残る()
    {
        var shelf = new TabShelfViewModel();
        shelf.Load([Editor(@"C:\work\a.cs"), Editor(@"C:\work\keep.cs", pinned: true)]);

        shelf.ClearCommand.Execute(null);

        Assert.Equal([@"C:\work\keep.cs"], shelf.Items.Select(i => i.Snapshot.FilePath));
    }

    [Fact]
    public void 棚はエディタとブラウザで区切って新しい順に並べる()
    {
        var shelf = new TabShelfViewModel();
        var browserOld = new ShelvedTabSnapshot { Kind = ShelvedTabSnapshot.BrowserKind, Url = "https://a/" };
        var editor = Editor(@"C:\work\a.cs");
        var browserNew = new ShelvedTabSnapshot { Kind = ShelvedTabSnapshot.BrowserKind, Url = "https://b/" };
        shelf.Add([browserOld]);
        shelf.Add([editor]);
        shelf.Add([browserNew]);

        Assert.Equal([editor], shelf.EditorItems.Select(i => i.Snapshot));
        Assert.Equal([browserNew, browserOld], shelf.BrowserItems.Select(i => i.Snapshot));
        Assert.True(shelf.HasEditorItems);

        shelf.Remove([editor.Id]);

        Assert.Empty(shelf.EditorItems);
        Assert.False(shelf.HasEditorItems);
        Assert.Equal(2, shelf.BrowserItems.Count);
    }

    [Fact]
    public void 棚は種類のグループの中に入りタブが無くても棚があればグループを出す()
    {
        var tabs = new TabsViewModel(new TabIconService(), new LoomoSettings());
        var editorKind = tabs.Kinds.Single(k => k.Kind == TabEntryKind.Editor);
        var terminalKind = tabs.Kinds.Single(k => k.Kind == TabEntryKind.Terminal);
        Assert.False(editorKind.HasContent);

        tabs.Shelf.Add([Editor(@"C:\work\a.cs")]);

        Assert.True(editorKind.HasShelfItems);
        Assert.True(editorKind.HasContent);
        Assert.Equal(1, editorKind.ShelfCount);
        Assert.False(editorKind.IsShelfExpanded);   // 既定は畳む
        Assert.Null(terminalKind.ShelfItems);        // ターミナルは棚を持たない
    }

    [Fact]
    public void すべて戻すは棚の全件を新しい順で要求する()
    {
        var shelf = new TabShelfViewModel();
        var a = Editor(@"C:\work\a.cs");
        var b = Editor(@"C:\work\b.cs");
        shelf.Add([b]);
        shelf.Add([a]);
        IReadOnlyList<ShelvedTabSnapshot>? requested = null;
        shelf.RestoreRequested += (_, items) => requested = items;

        shelf.RestoreAllCommand.Execute(null);

        Assert.Equal([a, b], requested);
    }

    [Fact]
    public void ターミナルのタブは棚へ下げられずピン留めもできない()
    {
        var tabs = new TabsViewModel(new TabIconService(), new LoomoSettings());
        var shelved = new List<TabEntryViewModel>();
        var pinned = new List<TabEntryViewModel>();
        tabs.TabShelveRequested += (_, t) => shelved.Add(t);
        tabs.TabPinToggled += (_, t) => pinned.Add(t);
        tabs.AddTerminalTab(Guid.NewGuid(), "pwsh", isActive: false);
        tabs.AddEditorTab(Guid.NewGuid(), @"C:\work\a.cs", isModified: false, isActive: false);

        tabs.ShelveTabCommand.Execute(tabs.TerminalTabs[0]);
        tabs.ShelveTabCommand.Execute(tabs.EditorTabs[0]);
        tabs.TogglePinTabCommand.Execute(tabs.TerminalTabs[0]);
        tabs.TogglePinTabCommand.Execute(tabs.EditorTabs[0]);

        Assert.Equal([tabs.EditorTabs[0]], shelved);
        Assert.Equal([tabs.EditorTabs[0]], pinned);
    }

    [Fact]
    public void 行のピン留め表示を合わせる()
    {
        var tabs = new TabsViewModel(new TabIconService(), new LoomoSettings());
        var id = Guid.NewGuid();
        tabs.AddBrowserTab(id, "Example", isActive: false);

        tabs.SetTabPinned(id, true);

        Assert.True(tabs.BrowserTabs.Single().IsPinned);
    }

    [Fact]
    public void 上限の選択は設定へ書かれ選んだものだけが選択中になる()
    {
        var settings = new LoomoSettings();
        var tabs = new TabsViewModel(new TabIconService(), settings);
        var changed = 0;
        tabs.ShelveLimitChanged += (_, _) => changed++;
        Assert.Single(tabs.ShelveLimitOptions, o => o.IsSelected && o.Count == 10);

        tabs.SelectShelveLimitCommand.Execute(tabs.ShelveLimitOptions.Single(o => o.Count == 5));

        Assert.Equal(5, settings.TabsPanel.AutoShelveLimit);
        Assert.Equal(5, tabs.AutoShelveLimit);
        Assert.Equal([5], tabs.ShelveLimitOptions.Where(o => o.IsSelected).Select(o => o.Count));
        Assert.Equal("5枚", tabs.ShelveLimitOptions.Single(o => o.Count == 5).Label);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void 見た時刻とピン留めはスナップショットに残る()
    {
        var snapshot = WorkspaceSessionCoordinator.CaptureBrowserTab(
            Guid.NewGuid(), "https://example.com/", "Example", isActive: false, T0, isPinned: true);

        Assert.Equal(T0, snapshot!.LastSeenUtc);
        Assert.True(snapshot.IsPinned);
        Assert.Null(WorkspaceSessionCoordinator.CaptureBrowserTab(Guid.NewGuid(), "https://a/", "A", false)!.LastSeenUtc);
    }

    private static ShelvedTabSnapshot Editor(string path, double shelvedDaysAgo = 0, bool pinned = false)
        => new()
        {
            Kind = ShelvedTabSnapshot.EditorKind, FilePath = path,
            ShelvedUtc = T0.AddDays(-shelvedDaysAgo), Pinned = pinned,
        };
}
