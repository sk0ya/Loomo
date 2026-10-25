using System;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.67 検索パネルのターミナル検索を、部屋の全部のターミナルのタブへ広げる——タブごとのまとめ方。</summary>
public sealed class TerminalSearchAcrossTabsTests
{
    private static readonly Guid TabA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TabB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static TerminalSearchHit Hit(Guid tab, string title, int line)
        => new(line, 0, 5, $"error {line}", tab, title);

    [Fact]
    public void Groups_hits_by_tab_keeping_the_provider_order()
    {
        var groups = SearchPanelViewModel.TerminalSearchGroups(
            [Hit(TabB, "build", 3), Hit(TabB, "build", 9), Hit(TabA, "pwsh", 1)]);

        Assert.Equal(new[] { "1. ターミナル「build」", "2. ターミナル「pwsh」" }, groups.Select(g => g.RelativePath));
        Assert.Equal(new[] { 2, 1 }, groups.Select(g => g.Count));
        Assert.All(groups[0].Matches, m => Assert.Equal(TabB, m.TerminalHit.TabId));
    }

    [Fact]
    public void A_single_tab_needs_no_number()
    {
        var groups = SearchPanelViewModel.TerminalSearchGroups([Hit(TabA, "pwsh", 1)]);
        Assert.Equal("ターミナル「pwsh」", Assert.Single(groups).RelativePath);
    }

    [Fact]
    public void A_slash_in_the_tab_title_does_not_become_a_folder()
    {
        var groups = SearchPanelViewModel.TerminalSearchGroups([Hit(TabA, "api/server", 1)]);
        var group = Assert.Single(groups);
        Assert.Equal("", group.FolderPath);
        Assert.Equal("ターミナル「api／server」", group.FileName);
    }

    [Fact]
    public void Hits_without_a_tab_fall_back_to_the_plain_heading()
    {
        var groups = SearchPanelViewModel.TerminalSearchGroups([new TerminalSearchHit(0, 0, 3, "foo")]);
        Assert.Equal("ターミナル", Assert.Single(groups).RelativePath);
    }
}
