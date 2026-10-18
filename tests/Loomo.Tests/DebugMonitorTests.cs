using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>IDE ペインのモニター（§28.15）——CPU 使用率の計算、メモリが増え続けているかの判定、記録の始め直しと印、
/// デバッグ対象を探すプロセスの親子。</summary>
public class DebugMonitorTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);
    private const long Mb = 1024 * 1024;

    private sealed class FakeSampler : IProcessSampler
    {
        public readonly Dictionary<int, Queue<ProcessSnapshot>> Next = new();
        public ProcessSnapshot? Read(int processId)
            => Next.TryGetValue(processId, out var q) && q.Count > 0 ? q.Dequeue() : null;
    }

    private static ProcessSnapshot Snap(int seconds, double cpuSeconds, long workingSetMb)
        => new(T0.AddSeconds(seconds), TimeSpan.FromSeconds(cpuSeconds), workingSetMb * Mb, workingSetMb * Mb, 12, 100, "App");

    [Fact]
    public void Cpu_percent_is_share_of_all_cores()
    {
        // 1 秒の間に CPU 時間が 2 秒進んだ・4 コア → 50%。
        Assert.Equal(50, ProcessMonitorMath.CpuPercent(Snap(0, 10, 100), Snap(1, 12, 100), 4), 3);
        Assert.Equal(0, ProcessMonitorMath.CpuPercent(Snap(1, 12, 100), Snap(1, 12, 100), 4));
    }

    private static List<ProcessMonitorSample> Series(Func<int, long> mbAt, int seconds = 120)
        => Enumerable.Range(0, seconds + 1)
            .Select(i => new ProcessMonitorSample(T0.AddSeconds(i), 1, mbAt(i) * Mb, mbAt(i) * Mb, 10, false))
            .ToList();

    [Fact]
    public void Steady_climb_is_sustained_growth()
    {
        var samples = Series(i => 300 + i * 3);   // 2 分で 300 → 660 MB
        Assert.Equal(360 * Mb, ProcessMonitorMath.SustainedGrowth(samples, T0.AddSeconds(120)));
    }

    [Fact]
    public void Sawtooth_and_small_growth_are_not_reported()
    {
        var saw = Series(i => 300 + (i % 30) * 15);              // GC で上下するだけ
        Assert.Null(ProcessMonitorMath.SustainedGrowth(saw, T0.AddSeconds(120)));
        var small = Series(i => 1000 + i);                         // +120 MB・12%
        Assert.Null(ProcessMonitorMath.SustainedGrowth(small, T0.AddSeconds(120)));
        var tooShort = Series(i => 300 + i * 10, seconds: 30);
        Assert.Null(ProcessMonitorMath.SustainedGrowth(tooShort, T0.AddSeconds(30)));
    }

    [Fact]
    public void View_model_restarts_per_process_and_marks_stops_and_end()
    {
        var sampler = new FakeSampler();
        sampler.Next[1] = new Queue<ProcessSnapshot>([Snap(0, 0, 100), Snap(1, 1, 110), Snap(2, 1, 110)]);
        sampler.Next[2] = new Queue<ProcessSnapshot>([Snap(10, 0, 50)]);
        var vm = new DebugMonitorViewModel(sampler, processorCount: 2);

        vm.Tick(1, false, T0);
        vm.Tick(1, false, T0.AddSeconds(1));
        Assert.Equal(2, vm.SamplesView.Count);
        Assert.Equal("50.0%", vm.CpuText);
        Assert.Equal("110 MB", vm.MemoryText);
        Assert.Contains("PID 1", vm.ProcessText);

        vm.Tick(1, true, T0.AddSeconds(2));
        Assert.Equal("停止", vm.MarkersView.Single().Label);
        Assert.True(vm.SamplesView[^1].IsStopped);

        vm.Tick(null, false, T0.AddSeconds(3));
        Assert.Equal("終了", vm.MarkersView[^1].Label);
        Assert.Contains("（終了）", vm.ProcessText);
        Assert.True(vm.HasData);                   // 終わった直後も眺められる

        vm.Tick(2, false, T0.AddSeconds(10));
        Assert.Single(vm.SamplesView);             // 次のセッションで始め直す
        Assert.Empty(vm.MarkersView);
    }

    [Fact]
    public void Growth_is_reported_once_per_session()
    {
        var sampler = new FakeSampler { Next = { [7] = new Queue<ProcessSnapshot>(
            Enumerable.Range(0, 130).Select(i => Snap(i, i * 0.1, 300 + i * 4))) } };
        var vm = new DebugMonitorViewModel(sampler, processorCount: 4);
        var reports = new List<string>();
        vm.GrowthDetected += reports.Add;

        for (var i = 0; i < 130; i++)
            vm.Tick(7, false, T0.AddSeconds(i));

        Assert.Single(reports);
        Assert.NotNull(vm.Warning);
    }

    [Fact]
    public void Process_tree_finds_a_child_of_this_process()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.Contains(child.Id, ProcessTree.ChildrenOf(Environment.ProcessId));
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
