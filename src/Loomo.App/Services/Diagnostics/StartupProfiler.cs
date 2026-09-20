using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 起動経路の各ステージ所要を計測してログへ即時追記する軽量プロファイラ（開発時のみ有効）。
/// プロセス生成からの経過（ランタイム/JIT/WPF 初期化を含む）も拾うため、最初のマーク時刻を
/// <see cref="Process.StartTime"/> 基準に補正する。各マークはすぐ <c>%APPDATA%/Loomo/startup.log</c>
/// へ追記するので、計測中にプロセスを落としても途中までのデータが残る。
/// 環境変数 <c>LOOMO_STARTUP_PROFILE=1</c> のときだけ記録する（既定は無効＝ノーオーバーヘッド）。
/// </summary>
internal static class StartupProfiler
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_STARTUP_PROFILE"), "1", StringComparison.Ordinal);

    private static readonly object Lock = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<(string Name, long AtMs, long DeltaMs)> Marks = new();

    /// <summary>プロセス生成 → 最初のマークまでの経過（ランタイム/JIT 起動分）。</summary>
    private static readonly long ProcessStartOffsetMs = ComputeProcessStartOffsetMs();

    private static long _lastMs;
    private static bool _begun;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "startup.log");

    private static long ComputeProcessStartOffsetMs()
    {
        try
        {
            var start = Process.GetCurrentProcess().StartTime;
            var ms = (long)(DateTime.Now - start).TotalMilliseconds;
            return ms > 0 ? ms : 0;
        }
        catch { return 0; }
    }

    /// <summary>1ステージの到達を記録する。<paramref name="name"/> は短い段階名。</summary>
    public static void Mark(string name)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            var now = ProcessStartOffsetMs + Clock.ElapsedMilliseconds;
            if (!_begun)
            {
                _begun = true;
                _lastMs = 0; // プロセス開始(0ms)からの差分として最初のマークを示す
                TryTruncate();
            }
            var delta = now - _lastMs;
            _lastMs = now;
            Marks.Add((name, now, delta));
            TryAppend($"{now,7} ms  (+{delta,6})  {name}");
        }
    }

    /// <summary>全マークの一覧（テストやデバッグ用）。</summary>
    public static IReadOnlyList<(string Name, long AtMs, long DeltaMs)> Snapshot()
    {
        lock (Lock) return Marks.ToList();
    }

    private static void TryTruncate()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"=== Loomo 起動プロファイル {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
        catch { /* 計測は補助。失敗は無視 */ }
    }

    private static void TryAppend(string line)
    {
        try { File.AppendAllText(LogPath, line + "\n"); }
        catch { /* 計測は補助。失敗は無視 */ }
    }
}
