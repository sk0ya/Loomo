using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;
using sk0ya.Loomo.Services.Debug.Js;
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

    [Fact]
    public async Task Stop_active_session_notifies_idle_then_exited()
    {
        var service = new NetcoredbgDebugService();
        typeof(NetcoredbgDebugService)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, DebugSessionState.Running);
        var events = new System.Collections.Generic.List<string>();
        service.StateChanged += (_, state) => events.Add($"state:{state}");
        service.Exited += (_, exited) => events.Add($"exited:{exited.Reason}");

        await service.StopAsync();

        Assert.Equal(["state:Idle", "exited:stop request"], events);
    }

    [Fact]
    public async Task Stop_idle_session_does_not_report_phantom_exit()
    {
        var service = new NetcoredbgDebugService();
        var exitCount = 0;
        service.Exited += (_, _) => exitCount++;

        await service.StopAsync();

        Assert.Equal(0, exitCount);
    }

    [Fact]
    public async Task Js_stop_active_session_notifies_idle_then_exited()
    {
        var service = new JsDebugService();
        typeof(JsDebugService)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, DebugSessionState.Running);
        var events = new System.Collections.Generic.List<string>();
        service.StateChanged += (_, state) => events.Add($"state:{state}");
        service.Exited += (_, exited) => events.Add($"exited:{exited.Reason}");

        await service.StopAsync();

        Assert.Equal(["state:Idle", "exited:stop request"], events);
    }
}
