using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class ExternalOpenRoutingTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ExternalOpenRequest Files(params string[] files) => new(null, files, []);

    [Fact]
    public void A_file_goes_to_the_room_that_contains_it_even_if_another_room_is_more_recent()
    {
        var owner = new RelayInstance(10, [@"C:\work\app"], T0);
        var recent = new RelayInstance(20, [@"C:\work\other"], T0.AddMinutes(5));

        var routes = ExternalOpenRouting.Route(Files(@"C:\work\app\src\a.cs"), [owner, recent]);

        var (pid, request) = Assert.Single(routes);
        Assert.Equal(10, pid);
        Assert.Equal([@"C:\work\app\src\a.cs"], request.Files);
    }

    [Fact]
    public void A_file_outside_every_room_goes_to_the_most_recent_room()
    {
        var older = new RelayInstance(10, [@"C:\work\app"], T0);
        var recent = new RelayInstance(20, [@"C:\work\other"], T0.AddMinutes(5));

        var routes = ExternalOpenRouting.Route(Files(@"D:\downloads\x.pdf"), [older, recent]);

        Assert.Equal(20, Assert.Single(routes).ProcessId);
    }

    [Fact]
    public void Prefix_without_separator_does_not_count_as_containing()
    {
        var app = new RelayInstance(10, [@"C:\work\app"], T0.AddMinutes(9));
        var app2 = new RelayInstance(20, [@"C:\work\app2"], T0);

        var routes = ExternalOpenRouting.Route(Files(@"C:\work\app2\a.cs"), [app, app2]);

        Assert.Equal(20, Assert.Single(routes).ProcessId);
    }

    [Fact]
    public void Files_in_added_folders_belong_to_that_room()
    {
        var multi = new RelayInstance(10, [@"C:\work\app", @"C:\libs\shared"], T0);
        var recent = new RelayInstance(20, [@"C:\work\other"], T0.AddMinutes(5));

        var routes = ExternalOpenRouting.Route(Files(@"C:\libs\shared\x.cs"), [multi, recent]);

        Assert.Equal(10, Assert.Single(routes).ProcessId);
    }

    [Fact]
    public void Urls_go_to_the_most_recent_room()
    {
        var older = new RelayInstance(10, [@"C:\a"], T0);
        var recent = new RelayInstance(20, [@"C:\b"], T0.AddMinutes(1));

        var routes = ExternalOpenRouting.Route(new(null, [], ["https://example.com/"]), [older, recent]);

        var (pid, request) = Assert.Single(routes);
        Assert.Equal(20, pid);
        Assert.Equal(["https://example.com/"], request.Urls);
    }

    [Fact]
    public void A_folder_already_open_activates_that_room_otherwise_opens_a_new_room()
    {
        var room = new RelayInstance(10, [@"C:\work\app"], T0);

        var existing = ExternalOpenRouting.Route(new(@"C:\work\app\", [], []), [room]);
        var fresh = ExternalOpenRouting.Route(new(@"C:\work\new", [], []), [room]);

        Assert.Equal(10, Assert.Single(existing).ProcessId);
        var (pid, request) = Assert.Single(fresh);
        Assert.Null(pid);
        Assert.Equal(@"C:\work\new", request.WorkspaceFolder);
    }

    [Fact]
    public void With_no_running_room_everything_is_opened_by_this_process()
    {
        var routes = ExternalOpenRouting.Route(
            new(null, [@"C:\x\a.txt", @"C:\y\b.txt"], ["https://example.com/"]), []);

        var (pid, request) = Assert.Single(routes);
        Assert.Null(pid);
        Assert.Equal(2, request.Files.Count);
        Assert.Single(request.Urls);
    }

    [Fact]
    public void Mixed_requests_are_split_per_room_with_this_process_last()
    {
        var a = new RelayInstance(10, [@"C:\a"], T0);
        var b = new RelayInstance(20, [@"C:\b"], T0.AddMinutes(1));

        var routes = ExternalOpenRouting.Route(
            new(@"C:\new", [@"C:\a\1.txt", @"C:\b\2.txt", @"C:\elsewhere\3.txt"], []), [a, b]);

        Assert.Equal([10, 20, (int?)null], routes.Select(r => r.ProcessId));
        Assert.Equal([@"C:\a\1.txt"], routes[0].Request.Files);
        Assert.Equal([@"C:\b\2.txt", @"C:\elsewhere\3.txt"], routes[1].Request.Files);
        Assert.Equal(@"C:\new", routes[2].Request.WorkspaceFolder);
    }
}
