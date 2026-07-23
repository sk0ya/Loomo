using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug.Js;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="JsDebugService"/> の実 js-debug（dapDebugServer.js）に対する統合テスト。
/// 親子 2 接続プロトコル（launch → startDebugging → 子セッションでの停止/検査/続行）の handshake を固定化する。
/// node または js-debug が未導入の環境では何も検証せずパスする（開発機・実機での回帰確認用）。
/// </summary>
public sealed class JsDebugServiceIntegrationTests
{
    [Fact]
    public async Task Launch_breakpoint_inspect_continue_exit()
    {
        if (!JsDebugAdapterLocator.IsInstalled) return;   // 未導入環境ではスキップ相当

        var dir = Directory.CreateTempSubdirectory("loomo-jsdbg-test-").FullName;
        try
        {
            var script = Path.Combine(dir, "app.js");
            await File.WriteAllTextAsync(script,
                "function add(a, b) {\n" +
                "  const sum = a + b;\n" +      // 2 行目にブレークポイント
                "  return sum;\n" +
                "}\n" +
                "console.log('result: ' + add(2, 3));\n");

            var service = new JsDebugService();
            var stopped = new TaskCompletionSource<DebugStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<DebugExited>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new List<DebugOutput>();
            service.Stopped += (_, e) => stopped.TrySetResult(e);
            service.Exited += (_, e) => exited.TrySetResult(e);
            service.Output += (_, e) => { lock (output) output.Add(e); };

            // 起動前にブレークポイントを記憶させる（構成フェーズで親子両接続へ送られる）。
            await service.SetBreakpointsAsync(script, new[] { new DebugBreakpoint(2) }, CancellationToken.None);
            await service.StartAsync(new DebugLaunchConfig(script, WorkingDirectory: dir), CancellationToken.None);

            // 子セッション成立 → ブレークポイント停止まで（コールド起動を見込んで長め）。
            var stop = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(2, stop.Line);
            Assert.NotNull(stop.SourcePath);
            Assert.Equal("app.js", Path.GetFileName(stop.SourcePath!));
            Assert.Equal(DebugSessionState.Stopped, service.State);

            // 検査：スタック先頭が add、ローカルに a/b が見える、式評価が効く。
            var frames = await service.GetStackTraceAsync();
            Assert.NotEmpty(frames);
            Assert.Contains("add", frames[0].Name);

            var scopes = await service.GetScopesAsync(frames[0].Id);
            Assert.NotEmpty(scopes);
            var vars = await service.GetVariablesAsync(scopes[0].VariablesReference);
            Assert.Contains(vars, v => v.Name == "a" && v.Value == "2");
            Assert.Contains(vars, v => v.Name == "b" && v.Value == "3");

            var eval = await service.EvaluateAsync("a + b", frames[0].Id);
            Assert.Equal("5", eval);

            // 続行 → 自然終了と console.log の出力（js-debug は exited イベントを送らないことがあるため
            // 終了コードは「無し or 0」を許容する）。
            await service.ContinueAsync();
            var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(exit.ExitCode is null or 0, $"exitCode={exit.ExitCode}");
            lock (output)
                Assert.Contains(output, o => o.Text.Contains("result: 5"));

            await service.StopAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
