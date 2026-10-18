namespace sk0ya.Loomo.App.Services;

/// <summary>あるプロセスの、ある瞬間の累計値（CPU 時間は累計なので、使用率は 2 点の差から出す）。</summary>
public sealed record ProcessSnapshot(
    DateTime Utc, TimeSpan TotalCpu, long WorkingSetBytes, long PrivateBytes, int Threads, int Handles, string Name);

/// <summary>モニターの 1 点（§28.15）。</summary>
public sealed record ProcessMonitorSample(
    DateTime Utc, double CpuPercent, long WorkingSetBytes, long PrivateBytes, int Threads, bool IsStopped);

/// <summary>モニターの印（ブレークポイントで止まった・動き出した・終わった）。</summary>
public sealed record ProcessMonitorMarker(DateTime Utc, string Label);

/// <summary>プロセスの値を読む口（テストでは差し替える）。</summary>
public interface IProcessSampler
{
    /// <summary>読めなければ（終わった・権限が無い）null。</summary>
    ProcessSnapshot? Read(int processId);
}

/// <summary><see cref="System.Diagnostics.Process"/> で読む。</summary>
public sealed class SystemProcessSampler : IProcessSampler
{
    public ProcessSnapshot? Read(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return null;
            return new ProcessSnapshot(DateTime.UtcNow, process.TotalProcessorTime, process.WorkingSet64,
                process.PrivateMemorySize64, process.Threads.Count, process.HandleCount, process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>モニターの計算（純ロジック・テスト対象・§28.15）。</summary>
public static class ProcessMonitorMath
{
    /// <summary>2 点の間の CPU 使用率（論理コア全部で 100%。タスクマネージャーと同じ物差し）。</summary>
    public static double CpuPercent(ProcessSnapshot previous, ProcessSnapshot current, int processorCount)
    {
        var wall = (current.Utc - previous.Utc).TotalMilliseconds;
        if (wall <= 0 || processorCount <= 0)
            return 0;
        var cpu = (current.TotalCpu - previous.TotalCpu).TotalMilliseconds;
        return Math.Clamp(cpu / (wall * processorCount) * 100, 0, 100);
    }

    /// <summary>メモリが増え続けているかを見る幅。</summary>
    public static readonly TimeSpan GrowthWindow = TimeSpan.FromMinutes(2);

    /// <summary>この幅で増えた量がこれ以上、かつ割合が <see cref="GrowthRatio"/> 以上なら「増え続けている」。</summary>
    public const long GrowthBytes = 200L * 1024 * 1024;
    public const double GrowthRatio = 0.5;

    /// <summary>
    /// 直近 <see cref="GrowthWindow"/> でワーキングセットが増え続けているなら、増えた量を返す（そうでなければ null）。
    /// 「増え続けている」は、幅の始まりから終わりまでに大きく増え、かつ途中の<b>谷が始まりより下がっていない</b>こと——
    /// GC で上下するだけのもの（のこぎり型）は拾わない。止まっている間（ブレークポイント）は数えない。
    /// </summary>
    public static long? SustainedGrowth(IReadOnlyList<ProcessMonitorSample> samples, DateTime nowUtc)
    {
        var window = samples.Where(s => !s.IsStopped && nowUtc - s.Utc <= GrowthWindow).ToList();
        if (window.Count < 10 || window[^1].Utc - window[0].Utc < GrowthWindow * 0.8)
            return null;
        var first = window[0].WorkingSetBytes;
        var last = window[^1].WorkingSetBytes;
        var grown = last - first;
        if (grown < GrowthBytes || first <= 0 || (double)grown / first < GrowthRatio)
            return null;
        // 後半の谷が前半の山より上にあること（右肩上がり）。
        var half = window.Count / 2;
        var lowLater = window.Skip(half).Min(s => s.WorkingSetBytes);
        var highEarlier = window.Take(half).Max(s => s.WorkingSetBytes);
        return lowLater > highEarlier ? grown : null;
    }

    /// <summary>「512 MB」「1.2 GB」。</summary>
    public static string Bytes(long bytes) => sk0ya.Loomo.Services.GitWorktreeCleanup.FormatSize(bytes);
}
