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
    /// <summary>npm スクリプトモード（runtimeExecutable=npm）の launch が通ることの統合確認。
    /// スクリプト経由でも子セッションが成立し、console 出力と自然終了が観測できること。</summary>
    [Fact]
    public async Task Launch_npm_script_outputs_and_exits()
    {
        if (!JsDebugAdapterLocator.IsInstalled) return;   // 未導入環境ではスキップ相当

        var dir = Directory.CreateTempSubdirectory("loomo-jsdbg-npm-test-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "app.js"),
                "console.log('npm-dev-ok: ' + (2 + 3));\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "package.json"),
                """{ "name": "npmtest", "scripts": { "dev": "node app.js" } }""");

            var service = new JsDebugService();
            var exited = new TaskCompletionSource<DebugExited>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new List<DebugOutput>();
            service.Exited += (_, e) => exited.TrySetResult(e);
            service.Output += (_, e) => { lock (output) output.Add(e); };

            await service.StartAsync(new DebugLaunchConfig("npm:dev", WorkingDirectory: dir), CancellationToken.None);

            var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(60));
            string all;
            lock (output) all = string.Join("\n", output.ConvertAll(o => $"[{o.Category}] {o.Text.TrimEnd()}"));
            Assert.True(exit.ExitCode is null or 0, $"exitCode={exit.ExitCode}\n--- output ---\n{all}");
            Assert.True(all.Contains("npm-dev-ok: 5"), $"出力に npm-dev-ok が無い:\n--- output ---\n{all}");

            await service.StopAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

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

    /// <summary>テストのデバッグ（§28.14）：vitest 本体をデバッガで起動し、テストファイル内のブレークポイントで
    /// 止まり、続行後に json レポートが書かれること。vitest を導入済みのパッケージを <c>LOOMO_VITEST_FIXTURE</c> で
    /// 渡したときだけ走る（<c>src/a.test.js</c> の 2 行目がテスト本体であること）。</summary>
    [Fact]
    public async Task Vitest_test_debug_stops_in_test_and_writes_report()
    {
        if (!JsDebugAdapterLocator.IsInstalled) return;
        if (Environment.GetEnvironmentVariable("LOOMO_VITEST_FIXTURE") is not { Length: > 0 } pkg) return;

        var report = Path.Combine(Path.GetTempPath(), $"loomo-vitest-debug-{Guid.NewGuid():N}.json");
        var testFile = Path.Combine(pkg, "src", "a.test.js");
        var launch = new sk0ya.Loomo.Services.Testing.VitestRunner().BuildDebugLaunch(
            new sk0ya.Loomo.Services.Testing.TestRunRequest(pkg, report,
                Targets: [new sk0ya.Loomo.Services.Testing.TestTarget(Path.Combine("src", "a.test.js"), "adds (x)")]),
            new sk0ya.Loomo.Services.Testing.TestToolLocator([pkg]));
        Assert.NotNull(launch);

        var service = new JsDebugService();
        var stopped = new TaskCompletionSource<DebugStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<DebugExited>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Stopped += (_, e) => stopped.TrySetResult(e);
        service.Exited += (_, e) => exited.TrySetResult(e);
        var output = new List<string>();
        service.Output += (_, e) => { lock (output) output.Add(e.Text.TrimEnd()); };
        string Log() { lock (output) return string.Join(Environment.NewLine, output); }
        try
        {
            await service.SetBreakpointsAsync(testFile, new[] { new DebugBreakpoint(2) }, CancellationToken.None);
            await service.StartAsync(new DebugLaunchConfig(launch.Program, launch.WorkingDirectory, launch.Args),
                CancellationToken.None);

            var first = await Task.WhenAny(stopped.Task, exited.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            Assert.True(first == stopped.Task, $"止まらなかった（{(first == exited.Task ? "終了" : "時間切れ")}）: {Log()}");
            var stop = await stopped.Task;
            Assert.Equal(2, stop.Line);
            Assert.Equal("a.test.js", Path.GetFileName(stop.SourcePath!));

            await Task.Delay(TimeSpan.FromSeconds(6));   // 既定の 5 秒を超えて止めても、タイムアウトで落ちない
            await service.ContinueAsync();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(60));

            var json = await File.ReadAllTextAsync(report);
            var results = sk0ya.Loomo.Services.Testing.JestJsonResultParser.Parse(json);
            var adds = Assert.Single(results, r => r.Title.EndsWith("adds (x)"));
            Assert.Equal(sk0ya.Loomo.Services.Testing.ScriptTestOutcome.Failed, adds.Outcome);
            Assert.Contains("expected 1 to be 2", adds.Message);   // タイムアウトではなく本来の失敗
        }
        finally
        {
            await service.StopAsync();   // 途中で落ちてもアダプタ（dapDebugServer）を残さない
            try { File.Delete(report); } catch { }
        }
    }
}
