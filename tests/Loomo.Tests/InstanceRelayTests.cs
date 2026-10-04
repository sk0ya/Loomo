using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class InstanceRelayTests
{
    private static string UniquePipe() => $"sk0ya.Loomo.Relay.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task Probe_answers_the_current_folders()
    {
        var name = UniquePipe();
        using var server = new InstanceRelayServer(name);
        server.SetFolders([@"C:\work\app", @"C:\libs\shared"]);

        var reply = await InstanceRelay.SendAsync(name, new InstanceRelay.Message("probe"), TimeSpan.FromSeconds(5));

        Assert.NotNull(reply);
        Assert.True(reply.Ok);
        Assert.Equal([@"C:\work\app", @"C:\libs\shared"], reply.Folders!);
        Assert.NotEqual(default, reply.LastActiveUtc);
    }

    [Fact]
    public async Task Requests_that_arrive_before_the_handler_are_queued_and_delivered()
    {
        var name = UniquePipe();
        using var server = new InstanceRelayServer(name);

        var reply = await InstanceRelay.SendAsync(name,
            new InstanceRelay.Message("open", Files: [@"C:\a.txt"], Urls: ["https://example.com/"]),
            TimeSpan.FromSeconds(5));
        Assert.True(reply?.Ok);

        var received = new List<ExternalOpenRequest>();
        server.SetHandler(received.Add);

        var request = Assert.Single(received);
        Assert.Equal([@"C:\a.txt"], request.Files);
        Assert.Equal(["https://example.com/"], request.Urls);
    }

    [Fact]
    public async Task Requests_after_the_handler_go_straight_to_it()
    {
        var name = UniquePipe();
        using var server = new InstanceRelayServer(name);
        var received = new TaskCompletionSource<ExternalOpenRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SetHandler(r => received.TrySetResult(r));

        await InstanceRelay.SendAsync(name, new InstanceRelay.Message("open", Folder: @"C:\work"), TimeSpan.FromSeconds(5));

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"C:\work", request.WorkspaceFolder);
    }

    [Fact]
    public async Task A_missing_room_is_reported_as_absent_not_thrown()
    {
        var reply = await InstanceRelay.SendAsync(UniquePipe(), new InstanceRelay.Message("probe"), TimeSpan.FromMilliseconds(200));

        Assert.Null(reply);
    }
}
