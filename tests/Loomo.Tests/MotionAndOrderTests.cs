using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Motion;
using sk0ya.Loomo.Core.Orders;
using sk0ya.Loomo.Services.Motion;
using sk0ya.Loomo.Services.Orders;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 動きで読む（§44.3）と言いつけ（§44.4）。見どころの読み方・許しの指紋／テスト結果（TRX・JUnit）の読み方／前後の比べ方
/// （成否の入れ替わり・無視する行・終了コード・悪い方への変化）／実際の git と PowerShell で録る（作業ツリーが変わらなければ録らない・
/// 言いつけで録ったものは「前」にしない）／言いつけ（裏のワークツリーで進める・人間の作業ツリーに触れない・採る・ぶつかるなら断る・
/// 捨てる・変更なしは片付ける・時刻のきっかけ）。裏の Claude Code は差し替える（本物は起こさない）。
/// </summary>
public sealed class MotionAndOrderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"loomo-motion-{Guid.NewGuid():N}");
    private readonly string _repo;

    public MotionAndOrderTests()
    {
        _repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_temp, recursive: true);
        }
        catch { /* 後始末の失敗は無視 */ }
    }

    private void Git(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
    }

    private void InitRepo()
    {
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.name", "test");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\ntwo\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    // ── 見どころ ─────────────────────────────────────────────

    [Fact]
    public void 見どころを読む_既定は_ターンごと_コマンド()
    {
        var probes = MotionProbes.Parse("""
            { "probes": [
              // コメントも許す
              { "id": "build", "command": "dotnet build", "ignore": ["経過時間"] },
              { "id": "tests", "title": "テスト", "kind": "tests", "command": "dotnet test", "when": "manual", "timeoutSeconds": 99999 },
            ] }
            """, out var error);

        Assert.Null(error);
        Assert.Equal(("build", "build", MotionProbeKind.Command, true), (probes![0].Id, probes[0].Title, probes[0].Kind, probes[0].EveryTurn));
        Assert.Equal(["経過時間"], probes[0].Ignore);
        Assert.Equal((MotionProbeKind.Tests, false, MotionProbe.MaxTimeoutSeconds), (probes[1].Kind, probes[1].EveryTurn, probes[1].TimeoutSeconds));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"probes\": [ { \"id\": \"a b\", \"command\": \"x\" } ] }")]
    [InlineData("{ \"probes\": [ { \"id\": \"a\", \"command\": \"x\" }, { \"id\": \"a\", \"command\": \"y\" } ] }")]
    [InlineData("{ \"probes\": [ { \"id\": \"a\", \"command\": \"\" } ] }")]
    [InlineData("{ \"probes\": [ { \"id\": \"a\", \"command\": \"x\", \"kind\": \"video\" } ] }")]
    [InlineData("{ \"probes\": [ { \"id\": \"a\", \"command\": \"x\", \"when\": \"always\" } ] }")]
    [InlineData("{ \"probes\": [ { \"id\": \"a\", \"command\": \"x\", \"ignore\": [\"(\"] } ] }")]
    public void 読めない見どころは理由つきで断る(string json)
    {
        Assert.Null(MotionProbes.Parse(json, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 許しの指紋は回る行で決まる()
    {
        var a = MotionProbes.Parse("""{ "probes": [ { "id": "b", "title": "B", "command": "x", "when": "manual" } ] }""", out _)!;
        var retitled = MotionProbes.Parse("""{ "probes": [ { "id": "b", "title": "別名", "command": "x", "ignore": ["t"] } ] }""", out _)!;
        var changed = MotionProbes.Parse("""{ "probes": [ { "id": "b", "command": "x --all" } ] }""", out _)!;

        Assert.Equal(MotionProbes.Fingerprint(a), MotionProbes.Fingerprint(retitled));
        Assert.NotEqual(MotionProbes.Fingerprint(a), MotionProbes.Fingerprint(changed));
    }

    [Fact]
    public void テスト結果を_TRX_と_JUnit_から読む()
    {
        var trx = MotionTestResults.Parse("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="A.Ok" outcome="Passed" />
              <UnitTestResult testName="A.Bad" outcome="Failed" />
              <UnitTestResult testName="A.Skip" outcome="NotExecuted" />
            </Results></TestRun>
            """);
        var junit = MotionTestResults.Parse("""
            <testsuites><testsuite name="s">
              <testcase classname="calc" name="adds" />
              <testcase classname="calc" name="divides"><failure message="x" /></testcase>
              <testcase name="later"><skipped /></testcase>
            </testsuite></testsuites>
            """);

        Assert.Equal([("A.Ok", "passed"), ("A.Bad", "failed"), ("A.Skip", "skipped")], trx.Select(t => (t.Name, t.Outcome)));
        Assert.Equal([("calc.adds", "passed"), ("calc.divides", "failed"), ("later", "skipped")], junit.Select(t => (t.Name, t.Outcome)));
        Assert.Empty(MotionTestResults.Parse("not xml"));
    }

    // ── 比べ方 ───────────────────────────────────────────────

    private static MotionSnapshot Snap(string id, params MotionSample[] samples)
        => new(id, DateTimeOffset.Now, null, 0, null, MotionSnapshot.TriggerTurn, samples);

    private static MotionSample Cmd(string output, int exit = 0) => new("build", "ビルド", MotionProbeKind.Command, exit, false, output, [], [], 1);

    private static MotionSample Tests(params (string Name, string Outcome)[] tests)
        => new("tests", "テスト", MotionProbeKind.Tests, 0, false, "", tests.Select(t => new MotionTest(t.Name, t.Outcome)).ToArray(), [], 1);

    [Fact]
    public void テストの入れ替わりを数え_落ちたものを先に並べる()
    {
        var before = Snap("1", Tests(("a", "passed"), ("b", "passed"), ("c", "failed"), ("gone", "passed")));
        var after = Snap("2", Tests(("a", "passed"), ("b", "failed"), ("c", "passed"), ("new", "passed")));

        var change = Assert.Single(MotionDiff.Compare(before, after, []));

        Assert.True(change.Changed);
        Assert.Equal(["b", "c", "gone", "new"], change.Flips.Select(f => f.Name));
        Assert.Equal(("passed", "failed"), (change.Flips[0].Before, change.Flips[0].After));
        Assert.Contains("1 件が落ちるようになった", change.Summary);
        Assert.Contains("1 件が通るようになった", change.Summary);
        Assert.True(MotionDiff.Worsened([change]));
    }

    [Fact]
    public void 無視する行を除けば同じなら_動きは同じ()
    {
        var probes = MotionProbes.Parse("""{ "probes": [ { "id": "build", "command": "x", "ignore": ["^経過時間"] } ] }""", out _)!;
        var same = MotionDiff.Compare(Snap("1", Cmd("ok\n経過時間 00:01  ")), Snap("2", Cmd("ok  \r\n経過時間 00:09")), probes);
        var differs = MotionDiff.Compare(Snap("1", Cmd("ok")), Snap("2", Cmd("ok\nwarning CS1")), probes);
        var broke = MotionDiff.Compare(Snap("1", Cmd("ok")), Snap("2", Cmd("ok", exit: 1)), probes);

        Assert.False(same[0].Changed);
        Assert.Equal("動きは同じ", same[0].Summary);
        Assert.Null(MotionDiff.Headline(same));
        Assert.True(differs[0].Changed);
        Assert.False(MotionDiff.Worsened(differs));
        Assert.Equal("ビルド：終了コード 0 → 1", MotionDiff.Headline(broke));
        Assert.True(MotionDiff.Worsened(broke));
    }

    [Fact]
    public void 前の動きが無ければ_そう言う()
    {
        var change = Assert.Single(MotionDiff.Compare(null, Snap("1", Cmd("ok")), []));
        Assert.False(change.HasBefore);
        Assert.False(change.Changed);
        Assert.False(MotionDiff.Worsened([change]));
    }

    // ── 録る（本物の git と PowerShell） ─────────────────────

    [Fact]
    public async Task 作業ツリーで見どころを回して録り_変わっていなければ録り直さない()
    {
        InitRepo();
        var store = new MotionStore(Path.Combine(_temp, "motion"));
        var probes = MotionProbes.Parse("""
            { "probes": [
              { "id": "count", "command": "(Get-Content a.txt).Count" },
              { "id": "tests", "kind": "tests", "command": "Set-Content -Path (Join-Path $env:LOOMO_OUT 'r.xml') -Value ('<testsuite><testcase name=\"lines\">' + $(if ((Get-Content a.txt).Count -gt 2) { '<failure/>' } else { '' }) + '</testcase></testsuite>')" }
            ] }
            """, out var error)!;
        Assert.Null(error);
        var recorder = new MotionRecorder(store);
        var origin = new MotionOrigin("s1", 7, "頼みごと", MotionSnapshot.TriggerTurn);

        var first = await recorder.RecordAsync(_repo, probes, origin, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("2", first!.Samples[0].Output.Trim());
        Assert.Equal([("lines", "passed")], first.Samples[1].Tests.Select(t => (t.Name, t.Outcome)));
        Assert.Null(await recorder.RecordAsync(_repo, probes, origin, CancellationToken.None, skipIfStamp: first.Stamp));

        File.AppendAllText(Path.Combine(_repo, "a.txt"), "three\n");
        await Task.Delay(1100);   // スナップショットの名前は時刻なので、同じ秒に重ねない
        var second = await recorder.RecordAsync(_repo, probes, origin, CancellationToken.None, skipIfStamp: first.Stamp);
        Assert.NotNull(second);

        var changes = store.Compare(_repo, second!, probes);
        Assert.Equal("3", second!.Samples[0].Output.Trim());
        Assert.True(changes.All(c => c.Changed));
        Assert.True(MotionDiff.Worsened(changes));
        Assert.Equal([second.Id, first.Id], store.List(_repo).Select(s => s.Id));
        Assert.Equal(("s1", 7), (store.Find(_repo, second.Id)!.SessionId, store.Find(_repo, second.Id)!.PromptLine));
    }

    [Fact]
    public async Task 言いつけで録った動きは_ほかの時点の前にしない()
    {
        var store = new MotionStore(Path.Combine(_temp, "motion"));
        store.Save(_repo, Snap("20261115-000001-000", Cmd("a")));
        store.Save(_repo, Snap("20261115-000002-000", Cmd("b")) with { OrderResultId = "r1" });
        var latest = Snap("20261115-000003-000", Cmd("c"));
        store.Save(_repo, latest);

        Assert.Equal("20261115-000001-000", store.Previous(_repo, latest)!.Id);
        Assert.Equal("20261115-000003-000", store.Latest(_repo)!.Id);
    }

    [Fact]
    public void 見どころの許しは_行が変わると外れる()
    {
        var store = new MotionStore(Path.Combine(_temp, "motion"));
        var probes = MotionProbes.Parse("""{ "probes": [ { "id": "b", "command": "x" } ] }""", out _)!;
        Assert.False(store.IsApproved(_repo, probes));
        store.Approve(_repo, probes);
        Assert.True(new MotionStore(Path.Combine(_temp, "motion")).IsApproved(_repo, probes));
        Assert.False(store.IsApproved(_repo, MotionProbes.Parse("""{ "probes": [ { "id": "b", "command": "y" } ] }""", out _)!));
        Assert.Contains("\"probes\"", File.ReadAllText(store.EnsureProbesFile(_repo)));
    }

    // ── 言いつけ ─────────────────────────────────────────────

    private OrderService Orders(Func<string, string, Task<int>> agent)
        => new(Path.Combine(_temp, "orders"), Path.Combine(_temp, "worktrees"))
        {
            RunAgent = async (folder, prompt, tools, timeout, cancel) => (await agent(folder, prompt), "まとめ：" + Path.GetFileName(folder)),
        };

    private static StandingOrder Order(string id = "tidy", string trigger = StandingOrder.Manual, string? at = null)
        => new(id, "片付けて", trigger, at, StandingOrder.DefaultTools, 3, 30);

    [Fact]
    public async Task 言いつけは裏のワークツリーで進み_人間の作業ツリーには触れない_採ると当たる()
    {
        InitRepo();
        string? seenPrompt = null;
        var orders = Orders((folder, prompt) =>
        {
            seenPrompt = prompt;
            File.WriteAllText(Path.Combine(folder, "a.txt"), "one\ntwo\nthree\n");
            File.WriteAllText(Path.Combine(folder, "new.txt"), "new\n");
            return Task.FromResult(0);
        });
        orders.Save(_repo, Order());

        var result = await orders.RunAsync(_repo, "tidy", "テストが落ちました", null, CancellationToken.None);

        Assert.Equal(OrderResult.Done, result.Status);
        Assert.Equal(2, result.Files);
        Assert.StartsWith("loomo/order/tidy/", result.Branch);
        Assert.True(Directory.Exists(result.Worktree));
        Assert.Contains("片付けて", seenPrompt);
        Assert.Contains("テストが落ちました", seenPrompt);
        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));   // 人間の作業ツリーはそのまま
        Assert.False(File.Exists(Path.Combine(_repo, "new.txt")));

        var taken = await orders.TakeAsync(_repo, result.Id, CancellationToken.None);

        Assert.Equal(OrderResult.Taken, taken.Status);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_repo, "new.txt")));
        Assert.False(Directory.Exists(result.Worktree));
        Assert.Equal(1, orders.Runs(_repo, "tidy", DateTime.Now).Today);
    }

    [Fact]
    public async Task ぶつかる仕上がりは採らずに_ファイルを挙げて断る()
    {
        InitRepo();
        var orders = Orders((folder, _) =>
        {
            File.WriteAllText(Path.Combine(folder, "a.txt"), "ONE\ntwo\n");
            return Task.FromResult(0);
        });
        orders.Save(_repo, Order());
        var result = await orders.RunAsync(_repo, "tidy", null, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "uno\ntwo\n");
        Git(_repo, "commit", "-qam", "human");

        var refused = await Assert.ThrowsAsync<OrderException>(() => orders.TakeAsync(_repo, result.Id, CancellationToken.None));

        Assert.Contains("a.txt", refused.Message);
        Assert.Equal("uno\ntwo\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));
        Assert.Equal(OrderResult.Done, orders.Results(_repo).Single().Status);
    }

    [Fact]
    public async Task 捨てるとワークツリーとブランチが消え_変更なしは片付けて残さない()
    {
        InitRepo();
        var write = true;
        var orders = Orders((folder, _) =>
        {
            if (write)
                File.WriteAllText(Path.Combine(folder, "b.txt"), "b\n");
            return Task.FromResult(0);
        });
        orders.Save(_repo, Order());
        var done = await orders.RunAsync(_repo, "tidy", null, null, CancellationToken.None);

        await orders.DiscardAsync(_repo, done.Id);

        Assert.Empty(orders.Results(_repo));
        Assert.False(Directory.Exists(done.Worktree));
        Assert.DoesNotContain("loomo/order", (await OrderService.GitAsync(_repo, CancellationToken.None, "branch", "--list")).Output);

        write = false;
        await Task.Delay(1100);
        var empty = await orders.RunAsync(_repo, "tidy", null, null, CancellationToken.None);
        Assert.Equal(OrderResult.Empty, empty.Status);
        Assert.Null(empty.Worktree);
        Assert.DoesNotContain("loomo/order", (await OrderService.GitAsync(_repo, CancellationToken.None, "branch", "--list")).Output);
    }

    [Fact]
    public async Task git_の外では失敗として盆に残す_同時には_1_本だけ()
    {
        var gate = new TaskCompletionSource<int>();
        var orders = Orders((_, _) => gate.Task);
        orders.Save(_repo, Order());
        var failed = await orders.RunAsync(_repo, "tidy", null, null, CancellationToken.None);
        Assert.Equal(OrderResult.Failed, failed.Status);
        Assert.Contains("git", failed.Summary);

        InitRepo();
        orders.Save(_repo, Order("other"));
        var first = orders.RunAsync(_repo, "tidy", null, null, CancellationToken.None);
        while (orders.Running is null)
            await Task.Delay(20);
        await Assert.ThrowsAsync<OrderException>(() => orders.RunAsync(_repo, "other", null, null, CancellationToken.None));
        gate.SetResult(0);
        Assert.Equal(OrderResult.Empty, (await first).Status);
    }

    [Fact]
    public void 言いつけの形と_時刻のきっかけ()
    {
        Assert.Null(StandingOrder.Validate("nightly-deps", "依存を上げて試す", StandingOrder.Daily, "07:30"));
        Assert.NotNull(StandingOrder.Validate("Nightly", "x", StandingOrder.Manual, null));
        Assert.NotNull(StandingOrder.Validate("a", " ", StandingOrder.Manual, null));
        Assert.NotNull(StandingOrder.Validate("a", "x", StandingOrder.Daily, "7:3"));
        Assert.NotNull(StandingOrder.Validate("a", "x", "hourly", null));

        var order = Order(trigger: StandingOrder.Daily, at: "07:30");
        var day = new DateTime(2026, 11, 16);
        Assert.False(order.IsDue(day.AddHours(7), null));
        Assert.True(order.IsDue(day.AddHours(8), null));
        Assert.True(order.IsDue(day.AddHours(8), day.AddDays(-1).AddHours(8)));
        Assert.False(order.IsDue(day.AddHours(9), day.AddHours(7.6)));
        Assert.False(Order().IsDue(day.AddHours(9), null));
    }
}
