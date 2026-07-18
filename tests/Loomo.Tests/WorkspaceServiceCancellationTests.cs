using System;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class WorkspaceServiceCancellationTests
{
    [Fact]
    public async Task ListAsync_honors_pre_canceled_token()
    {
        var service = new WorkspaceService(new SafetySettings());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ListAsync(".", new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ReadFileAsync_honors_pre_canceled_token()
    {
        var service = new WorkspaceService(new SafetySettings());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ReadFileAsync("missing.txt", new CancellationToken(canceled: true)));
    }
}
