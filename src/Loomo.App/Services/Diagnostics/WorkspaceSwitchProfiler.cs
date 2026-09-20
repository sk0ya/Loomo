using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ワークスペース切替の区間時間を測る開発用プロファイラ。
/// <c>LOOMO_WORKSPACE_PROFILE=1</c> のときだけ有効で、切替完了時にまとめて1回だけログへ追記する。
/// 計測中のファイル I/O が各区間へ混ざらないよう、<see cref="Lap"/> はメモリへ記録するだけにする。
/// </summary>
internal sealed class WorkspaceSwitchProfiler : IDisposable
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_WORKSPACE_PROFILE"), "1",
            StringComparison.Ordinal);
    private static readonly object LogLock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "workspace-switch.log");

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _target;
    private readonly List<(string Name, double Ms)> _laps = [];
    private long _lastTicks;

    private WorkspaceSwitchProfiler(string target) => _target = target;

    public static WorkspaceSwitchProfiler? Begin(string target)
        => Enabled ? new WorkspaceSwitchProfiler(target) : null;

    public void Lap(string name)
    {
        var now = _clock.ElapsedTicks;
        _laps.Add((name, (now - _lastTicks) * 1000d / Stopwatch.Frequency));
        _lastTicks = now;
    }

    public void Dispose()
    {
        _clock.Stop();
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append("  target=").Append(_target)
            .Append("  total=").Append(_clock.Elapsed.TotalMilliseconds.ToString("0.0")).Append(" ms");
        foreach (var (name, ms) in _laps)
            line.Append("  ").Append(name).Append('=').Append(ms.ToString("0.0"));

        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line.AppendLine().ToString());
            }
            catch { /* 計測は補助。失敗は切替を妨げない */ }
        }
    }
}
