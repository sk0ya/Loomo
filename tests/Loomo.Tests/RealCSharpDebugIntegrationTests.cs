using System.IO;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.CSharp.Testing;
using sk0ya.Loomo.Services.Debug;
using Xunit.Sdk;

namespace sk0ya.Loomo.Tests;

/// <summary>VSTestのdebug hostとnetcoredbgの実接続確認。
/// 通常のテストでは外部デバッガを起動せず、<c>LOOMO_RUN_REAL_DEBUG=1</c> のときだけ実行する。</summary>
[Collection(CSharpExternalProcessCollection.Name)]
public sealed class RealCSharpDebugIntegrationTests
{
    [RealDebugFact]
    public async Task Netcoredbg_attaches_to_vstest_host_and_test_finishes()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "sk0ya.Loomo.Tests.dll");
        Assert.True(File.Exists(assembly), $"テストアセンブリがありません: {assembly}");

        await using var runner = await CSharpTestDebugProcess.StartAsync(
            assembly,
            "FullyQualifiedName~CSharpTestDebugRunnerTests.Parses_testhost_process_id");
        Assert.True(runner.TestHostProcessId is > 0);

        var debug = new NetcoredbgDebugService();
        var exited = new TaskCompletionSource<DebugExited>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Exited += (_, result) => exited.TrySetResult(result);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await debug.AttachAsync(
                new DebugAttachConfig(runner.TestHostProcessId!.Value, "testhost"), timeout.Token);
            Assert.True(debug.State is DebugSessionState.Running or DebugSessionState.Stopped,
                $"attach後の状態が不正です: {debug.State}");

            await debug.ContinueAsync(timeout.Token);
            var result = await exited.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, result.ExitCode);
            await debug.WaitForIdleAsync(timeout.Token);
        }
        finally
        {
            await debug.StopAsync(CancellationToken.None);
            await debug.WaitForIdleAsync(CancellationToken.None);
        }
    }

    /// <summary>通常のCIではnetcoredbgを起動せず、明示的な環境変数でだけ実行するFact。</summary>
    private sealed class RealDebugFactAttribute : FactAttribute
    {
        public RealDebugFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("LOOMO_RUN_REAL_DEBUG"), "1",
                    StringComparison.Ordinal))
                Skip = "LOOMO_RUN_REAL_DEBUG=1 のときだけ実netcoredbgを起動します。";
        }
    }
}
