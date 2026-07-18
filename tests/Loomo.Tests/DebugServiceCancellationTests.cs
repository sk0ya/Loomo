using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class DebugServiceCancellationTests
{
    [Fact]
    public void All_async_debug_operations_accept_cancellation_token()
    {
        var missing = typeof(IDebugService).GetMethods()
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
            .Where(method => method.GetParameters().LastOrDefault()?.ParameterType != typeof(CancellationToken))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Query_honors_pre_canceled_token_without_active_session()
    {
        var service = new NetcoredbgDebugService();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetThreadsAsync(new CancellationToken(canceled: true)));
    }
}
