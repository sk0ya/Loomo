using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>サイドバーの TABS（タブ一覧）に「どの種別を出すか」。隠すのは表示だけで、タブ自体は
/// 閉じない——ここではその区別と、見出しの件数が並んでいる行と食い違わないことを見る
/// （見出しの設定ボタン＝⚙ で開くポップアップの見た目は <c>TabsView.xaml</c>）。</summary>
public sealed class TabsViewModelTests
{
    private static TabsViewModel Sut(LoomoSettings? settings = null)
        => new(new TabIconService(), settings);

    private static TabsViewModel WithOneOfEach(LoomoSettings? settings = null)
    {
        var sut = Sut(settings);
        sut.AddEditorTab(Guid.NewGuid(), @"C:\work\a.cs", isModified: false, isActive: true);
        sut.AddBrowserTab(Guid.NewGuid(), "Bing", isActive: false);
        sut.AddTerminalTab(Guid.NewGuid(), "pwsh", isActive: false);
        return sut;
    }

    [Fact]
    public void 既定は種別を3つとも出す()
    {
        var sut = WithOneOfEach();

        Assert.True(sut.ShowEditorTabs);
        Assert.True(sut.ShowBrowserTabs);
        Assert.True(sut.ShowTerminalTabs);
        Assert.True(sut.IsEditorSectionVisible);
        Assert.True(sut.IsBrowserSectionVisible);
        Assert.True(sut.IsTerminalSectionVisible);
        Assert.False(sut.IsAllKindsHidden);
        Assert.Equal(3, sut.TotalCount);
    }

    [Fact]
    public void 隠した種別は並べないがタブは閉じない()
    {
        var sut = WithOneOfEach();

        sut.ShowTerminalTabs = false;

        Assert.False(sut.IsTerminalSectionVisible);
        // 一覧に出さないだけ——ペインのタブ列は動かさないので、持っている行はそのまま。
        Assert.Single(sut.TerminalTabs);
        Assert.Equal(2, sut.TotalCount);
    }

    [Fact]
    public void 出す設定でもそのタブが無ければ並べない()
    {
        var sut = Sut();

        Assert.True(sut.ShowBrowserTabs);
        Assert.False(sut.IsBrowserSectionVisible);
        Assert.Equal(0, sut.TotalCount);
    }

    [Fact]
    public void 件数は出している種別だけを数える()
    {
        var sut = Sut();
        var changed = new List<string?>();
        sut.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        sut.AddEditorTab(Guid.NewGuid(), @"C:\work\a.cs", isModified: false, isActive: true);
        Assert.Equal(1, sut.TotalCount);
        Assert.Contains(nameof(TabsViewModel.TotalCount), changed);

        // 種別を隠した時点でも見出しは数え直す（行が減ったのに数だけ残ると見出しが嘘をつく）。
        changed.Clear();
        sut.ShowEditorTabs = false;
        Assert.Equal(0, sut.TotalCount);
        Assert.Contains(nameof(TabsViewModel.TotalCount), changed);
        Assert.Contains(nameof(TabsViewModel.IsEditorSectionVisible), changed);
    }

    [Fact]
    public void 種別を3つとも隠したときだけ案内を出す()
    {
        var sut = WithOneOfEach();

        sut.ShowEditorTabs = false;
        sut.ShowBrowserTabs = false;
        Assert.False(sut.IsAllKindsHidden);

        sut.ShowTerminalTabs = false;
        Assert.True(sut.IsAllKindsHidden);
    }

    [Fact]
    public void 選択は設定へ書き戻す()
    {
        var settings = new LoomoSettings();
        var sut = Sut(settings);

        sut.ShowBrowserTabs = false;

        Assert.False(settings.TabsPanel.ShowBrowser);
        Assert.True(settings.TabsPanel.ShowEditor);
        Assert.True(settings.TabsPanel.ShowTerminal);
    }

    [Fact]
    public void 保存された選択で起動する()
    {
        var settings = new LoomoSettings();
        settings.TabsPanel.ShowEditor = false;
        settings.TabsPanel.ShowTerminal = false;

        var sut = WithOneOfEach(settings);

        Assert.False(sut.IsEditorSectionVisible);
        Assert.True(sut.IsBrowserSectionVisible);
        Assert.False(sut.IsTerminalSectionVisible);
        Assert.Equal(1, sut.TotalCount);
        // 起動しただけでは書き戻さない（復元した値をそのまま保つ）。
        Assert.False(settings.TabsPanel.ShowEditor);
    }

    [Fact]
    public void 種別行は一覧と同じ並びで名前と件数を持つ()
    {
        var sut = WithOneOfEach();

        Assert.Equal(
            [TabEntryKind.Editor, TabEntryKind.Browser, TabEntryKind.Terminal],
            sut.Kinds.Select(k => k.Kind));
        Assert.Equal(["エディタ", "ブラウザ", "ターミナル"], sut.Kinds.Select(k => k.Label));
        // 隠していても件数は出し続ける（「無くなった」に見せない）。
        sut.ShowEditorTabs = false;
        Assert.All(sut.Kinds, k => Assert.Equal(1, k.Count));
    }

    [Fact]
    public void 種別行は対応するタブ群をそのまま持つ()
    {
        var sut = WithOneOfEach();

        Assert.Same(sut.EditorTabs, sut.Kinds[0].Tabs);
        Assert.Same(sut.BrowserTabs, sut.Kinds[1].Tabs);
        Assert.Same(sut.TerminalTabs, sut.Kinds[2].Tabs);
    }

    [Fact]
    public void 種別行を押すと出し入れが切り替わり設定へ書き戻す()
    {
        var settings = new LoomoSettings();
        var sut = WithOneOfEach(settings);
        var editorRow = sut.Kinds.Single(k => k.Kind == TabEntryKind.Editor);

        editorRow.IsShown = false;

        // 行が正本で、ShowEditorTabs はその窓口（二重の真実を作らない）。
        Assert.False(sut.ShowEditorTabs);
        Assert.False(sut.IsEditorSectionVisible);
        Assert.False(settings.TabsPanel.ShowEditor);
        Assert.Equal(2, sut.TotalCount);
    }

    [Fact]
    public void 一つでも隠していれば絞りの印を立てる()
    {
        var sut = WithOneOfEach();

        Assert.False(sut.IsFiltered);

        sut.ShowBrowserTabs = false;
        Assert.True(sut.IsFiltered);

        sut.ShowBrowserTabs = true;
        Assert.False(sut.IsFiltered);
    }
}
