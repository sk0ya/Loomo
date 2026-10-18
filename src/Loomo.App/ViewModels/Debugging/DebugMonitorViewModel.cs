using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// IDE ペインの「モニター」タブ（§28.15）。実行中・デバッグ中のプロセスの CPU とメモリを時系列で持ち、止まった・動き出した・
/// 終わったに印を付ける。Rider の Monitoring ツールウィンドウ（2026.2.3）の、AI を除いた部分。
/// <para>時計は持たない——<see cref="Tick"/> を呼ぶ側（ShellWindow）が 1 秒ごとに回す。走っているセッションが無い間は呼ばれず、
/// 最後の記録は次のセッションが始まるまで残る（終わった直後に眺められるように）。</para>
/// </summary>
public sealed partial class DebugMonitorViewModel : ObservableObject
{
    /// <summary>残す長さ（1 秒 1 点で 10 分）。</summary>
    public const int MaxSamples = 600;

    private readonly IProcessSampler _sampler;
    private readonly int _processorCount;
    private readonly List<ProcessMonitorSample> _samples = new();
    private readonly List<ProcessMonitorMarker> _markers = new();
    private ProcessSnapshot? _previous;
    private int? _processId;
    private bool _wasStopped;
    private bool _growthReported;

    public DebugMonitorViewModel(IProcessSampler? sampler = null, int? processorCount = null)
    {
        _sampler = sampler ?? new SystemProcessSampler();
        _processorCount = processorCount ?? Environment.ProcessorCount;
    }

    /// <summary>描く点（古い順）。差し替えるたびに新しいリストになる（グラフは参照の変化で描き直す）。</summary>
    [ObservableProperty] private IReadOnlyList<ProcessMonitorSample> _samplesView = Array.Empty<ProcessMonitorSample>();

    [ObservableProperty] private IReadOnlyList<ProcessMonitorMarker> _markersView = Array.Empty<ProcessMonitorMarker>();

    [ObservableProperty] private string _processText = "実行中のプロセスはありません";
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private string _peakText = "";
    [ObservableProperty] private string _threadsText = "";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private bool _hasData;

    /// <summary>メモリが増え続けていると判断した（1 セッションに 1 回）。ホストが通知欄へ出す。</summary>
    public event Action<string>? GrowthDetected;

    /// <summary>
    /// 1 回分読む。<paramref name="processId"/> が前回と変われば記録を始め直す（次のセッション）。null なら何も読まず、
    /// 走っていたものが終わったなら「終了」の印を付ける。
    /// </summary>
    public void Tick(int? processId, bool isStopped, DateTime nowUtc)
    {
        if (processId is null)
        {
            if (_processId is not null)
            {
                AddMarker(nowUtc, "終了");
                ProcessText = ProcessText.Replace("（実行中）", "（終了）");
                _processId = null;
                _previous = null;
                Publish();
            }
            return;
        }

        if (processId != _processId)
            Restart(processId.Value);

        if (_sampler.Read(processId.Value) is not { } snapshot)
            return;
        var cpu = _previous is { } prev ? ProcessMonitorMath.CpuPercent(prev, snapshot, _processorCount) : 0;
        _previous = snapshot;
        ProcessText = $"{snapshot.Name}（PID {processId}）（実行中）";

        if (isStopped != _wasStopped)
        {
            AddMarker(nowUtc, isStopped ? "停止" : "再開");
            _wasStopped = isStopped;
        }

        _samples.Add(new ProcessMonitorSample(nowUtc, isStopped ? 0 : cpu, snapshot.WorkingSetBytes,
            snapshot.PrivateBytes, snapshot.Threads, isStopped));
        if (_samples.Count > MaxSamples)
            _samples.RemoveRange(0, _samples.Count - MaxSamples);

        CpuText = $"{cpu:0.0}%";
        MemoryText = ProcessMonitorMath.Bytes(snapshot.WorkingSetBytes);
        PeakText = ProcessMonitorMath.Bytes(_samples.Max(s => s.WorkingSetBytes));
        ThreadsText = $"{snapshot.Threads}";

        if (!_growthReported && ProcessMonitorMath.SustainedGrowth(_samples, nowUtc) is { } grown)
        {
            _growthReported = true;
            Warning = $"メモリが増え続けています（{ProcessMonitorMath.GrowthWindow.TotalMinutes:0}分で +{ProcessMonitorMath.Bytes(grown)}）";
            GrowthDetected?.Invoke($"{snapshot.Name}: {Warning}");
        }
        Publish();
    }

    private void Restart(int processId)
    {
        _processId = processId;
        _previous = null;
        _samples.Clear();
        _markers.Clear();
        _wasStopped = false;
        _growthReported = false;
        Warning = null;
    }

    private void AddMarker(DateTime nowUtc, string label)
    {
        _markers.Add(new ProcessMonitorMarker(nowUtc, label));
        if (_samples.Count > 0)
            _markers.RemoveAll(m => m.Utc < _samples[0].Utc);
    }

    private void Publish()
    {
        SamplesView = _samples.ToArray();
        MarkersView = _markers.ToArray();
        HasData = _samples.Count > 0;
    }
}
