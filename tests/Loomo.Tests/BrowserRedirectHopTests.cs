using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class BrowserRedirectHopTests
{
    private static readonly DateTime Completed = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Script_navigation_right_after_load_is_a_hop()
        => Assert.True(BrowserRedirectHop.IsHop(
            false, CoreWebView2NavigationKind.NewDocument, Completed, Completed.AddMilliseconds(300)));

    [Fact]
    public void User_or_host_initiated_navigation_is_not_a_hop()
        => Assert.False(BrowserRedirectHop.IsHop(
            true, CoreWebView2NavigationKind.NewDocument, Completed, Completed.AddMilliseconds(300)));

    [Theory]
    [InlineData(CoreWebView2NavigationKind.Reload)]
    [InlineData(CoreWebView2NavigationKind.BackOrForward)]
    public void Reload_and_history_moves_are_not_hops(CoreWebView2NavigationKind kind)
        => Assert.False(BrowserRedirectHop.IsHop(false, kind, Completed, Completed.AddMilliseconds(300)));

    [Fact]
    public void Script_navigation_after_the_page_was_read_is_not_a_hop()
        => Assert.False(BrowserRedirectHop.IsHop(
            false, CoreWebView2NavigationKind.NewDocument, Completed, Completed.AddSeconds(30)));

    [Fact]
    public void First_navigation_of_a_tab_is_not_a_hop()
        => Assert.False(BrowserRedirectHop.IsHop(
            false, CoreWebView2NavigationKind.NewDocument, null, Completed));
}
