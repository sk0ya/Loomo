using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>起動か、それともアタッチで始まったセッションか（再起動でどちらを再現するかの判断に使う）。</summary>
public enum DebugSessionKind { Launch, Attach }

/// <summary>1 本のデバッグセッション（1 netcoredbg プロセス＝1 <see cref="IDebugService"/> インスタンス）分の状態。
/// <see cref="DebugViewModel"/>（セッションマネージャ）が <see cref="DebugViewModel.Sessions"/> にいくつも保持し、
/// <see cref="DebugViewModel.ActiveSession"/> がどれをデバッグペインへ見せるかを選ぶ。
///
/// コンソール出力・状態文言・実行中/停止中・検査（コールスタック/変数/スレッド/ウォッチ/イミディエイト/モジュール）は
/// このセッション固有。ブレークポイント・起動構成・プロファイル・テストは（Rider 同様）全セッションで共有する側なので
/// ここには持たず、<see cref="DebugViewModel"/> 側に居続ける。<see cref="IDebugSession.IsTaskRunning"/>／
/// <see cref="IDebugSession.IsAdapterMissing"/> もビルド/テストの排他とアダプタ導入状況で全セッション共有のため、
/// マネージャへそのまま委譲する。</summary>
public sealed partial class DebugSessionViewModel : ObservableObject, IDebugSession, IDisposable
{
    private readonly DebugManagerViewModelBase _manager;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;
    private Stopwatch? _sessionClock;
    private bool _firstStopReported;
    private bool _userStopRequested;
    private bool _reachedRunning;

    public Guid SessionId { get; } = Guid.NewGuid();
    public IDebugService DebugService { get; }
    public DebugSessionKind Kind { get; internal set; } = DebugSessionKind.Launch;

    /// <summary>アタッチで始まった場合の対象プロセス（再起動で再アタッチする対象）。起動なら null。</summary>
    internal DebugProcessViewModel? AttachedProcess { get; set; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _statusMessage = "待機中";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStopped;

    public ObservableCollection<DebugOutputLine> Output { get; } = new();

    public DebugInspectionViewModel Inspection { get; }

    // --- エディタ連携の通知（アクティブなセッションのものだけ DebugViewModel が中継する） ---
    public event Action<string?, int>? ExecutionLineChanged;
    public event Action<bool>? StoppedChanged;
    public event Action<string, int>? FramePreviewRequested;
    public event Action<string, int>? FrameActivated;

    internal DebugSessionViewModel(IDebugService debug, DebugManagerViewModelBase manager, string displayName)
    {
        DebugService = debug;
        _manager = manager;
        _displayName = displayName;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Inspection = new DebugInspectionViewModel(debug, this);

        debug.Output += OnOutput;
        debug.StateChanged += OnStateChanged;
        debug.Stopped += OnStopped;
        debug.Continued += OnContinued;
        debug.Exited += OnDebugExited;
    }

    // --- IDebugSession 実装（Inspection が使う窓口。ビルド/テスト・起動構成は共有側なのでマネージャへ委譲） ---

    bool IDebugSession.IsBusy => IsBusy;
    bool IDebugSession.IsStopped => IsStopped;
    bool IDebugSession.IsTaskRunning { get => _manager.IsTaskRunning; set => _manager.IsTaskRunning = value; }
    bool IDebugSession.IsAdapterMissing => _manager.IsAdapterMissing;
    string IDebugSession.StatusMessage { get => StatusMessage; set => StatusMessage = value; }

    public event Action? SessionStateChanged;

    partial void OnIsBusyChanged(bool value) => SessionStateChanged?.Invoke();
    partial void OnIsStoppedChanged(bool value)
    {
        StoppedChanged?.Invoke(value);
        SessionStateChanged?.Invoke();
    }

    void IDebugSession.RefreshAdapter() => _manager.Refresh();

    CancellationToken IDebugSession.BeginSession()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _sessionClock = Stopwatch.StartNew();
        _firstStopReported = false;
        _userStopRequested = false;
        _reachedRunning = false;
        IdeQualityDiagnosticLog.Write("debug.start.requested", $"session={SessionId} name={DisplayName} kind={Kind}");
        return _cts.Token;
    }

    void IDebugSession.CancelSession()
    {
        _userStopRequested = true;
        _cts?.Cancel();
    }

    // 出力タブへの切替・ブレークポイントガター再同期はセッション非依存（マネージャ／Breakpoints VM が持つ）ため未使用。
    void IDebugSession.RequestOutput() { }
    void IDebugSession.RaiseBreakpointsRefreshed(string path) { }

    void IDebugSession.RaiseExecutionLine(string? path, int line0) => ExecutionLineChanged?.Invoke(path, line0);
    void IDebugSession.RaiseFramePreview(string path, int line0) => FramePreviewRequested?.Invoke(path, line0);
    void IDebugSession.RaiseFrameActivated(string path, int line0) => FrameActivated?.Invoke(path, line0);
    string? IDebugSession.FindBuildTarget() => _manager.FindBuildTarget();

    /// <summary>共有の <see cref="DebugLaunchViewModel"/>（「次のステートメントに設定」等）から、
    /// このセッションの実行行ハイライトを直接動かす。</summary>
    internal void NotifyExecutionLine(string? path, int line0) => ExecutionLineChanged?.Invoke(path, line0);

    /// <summary>共有の窓口経由でこのセッションのフレームプレビューを動かす（現状未使用の防御的実装）。</summary>
    internal void NotifyFramePreview(string path, int line0) => FramePreviewRequested?.Invoke(path, line0);

    void IDebugSession.Append(DebugOutputCategory category, string text) => Append(category, text);
    void IDebugSession.ReportBuildOutput(string output) => _manager.ReportBuildOutput(output);

    private void Append(DebugOutputCategory category, string text)
    {
        Output.Add(new DebugOutputLine(category, text));
        const int max = 2000;
        if (Output.Count > max) Output.RemoveAt(0);
    }

    public void WriteConsole(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) Append(DebugOutputCategory.Console, t);
        }
    }

    // --- デバッグアダプタ（DAP）イベント → 状態更新 ---

    private void OnOutput(object? sender, DebugOutput e)
        => Dispatch(() => Append(e.Category, e.Text.TrimEnd('\r', '\n')));

    private void OnStopped(object? sender, DebugStopped e)
        => Dispatch(async () =>
        {
            if (!_firstStopReported)
            {
                _firstStopReported = true;
                IdeQualityDiagnosticLog.Write("debug.first-stop",
                    $"session={SessionId} elapsedMs={_sessionClock?.ElapsedMilliseconds ?? 0} reason={e.Reason} path={e.SourcePath} line={e.Line}");
            }
            var reasonText = NormalizeStopReason(e);
            StatusMessage = reasonText;
            if (!string.IsNullOrEmpty(e.SourcePath) && e.Line > 0)
            {
                Append(DebugOutputCategory.Important,
                    $"{reasonText}: {Path.GetFileName(e.SourcePath)}:{e.Line}");
                ExecutionLineChanged?.Invoke(e.SourcePath, e.Line - 1);  // DAP 1始まり → エディタ 0始まり
            }
            else
            {
                Append(DebugOutputCategory.Important, reasonText);
            }
            await Inspection.OnStoppedAsync();
        });

    internal static string NormalizeStopReason(DebugStopped stopped)
    {
        if (stopped.Reason.Equals("exception", StringComparison.OrdinalIgnoreCase))
        {
            var name = string.IsNullOrWhiteSpace(stopped.ExceptionName) ? "例外" : stopped.ExceptionName;
            return string.IsNullOrWhiteSpace(stopped.ExceptionMessage)
                ? $"例外停止: {name}"
                : $"例外停止: {name} — {stopped.ExceptionMessage}";
        }
        var reason = stopped.Reason switch
        {
            "breakpoint" => "ブレークポイント",
            "step" => "ステップ完了",
            "pause" => "一時停止",
            _ => stopped.Reason,
        };
        return $"停止: {reason}";
    }

    private void OnContinued(object? sender, EventArgs e)
        => Dispatch(() => { ExecutionLineChanged?.Invoke(null, -1); Inspection.Clear(); });

    private void OnDebugExited(object? sender, DebugExited e)
        => Dispatch(() =>
        {
            _sessionClock?.Stop();
            var elapsed = _sessionClock?.Elapsed;
            var outcome = DebugSessionOutcome.Classify(e.ExitCode, e.Reason, _userStopRequested, _reachedRunning);
            var code = outcome.Summary;
            var duration = elapsed is { } time ? $"、実行時間 {FormatDuration(time)}" : "";
            Append(DebugOutputCategory.Important, $"セッション終了（{code}{duration}）");
            StatusMessage = $"{code} — {outcome.NextAction}";
            IdeQualityDiagnosticLog.Write("debug.ended",
                $"session={SessionId} elapsedMs={elapsed?.TotalMilliseconds ?? 0:0} code={e.ExitCode?.ToString() ?? "unknown"} reason={e.Reason}");
            ExecutionLineChanged?.Invoke(null, -1);
            Inspection.Clear();
        });

    private static string FormatDuration(TimeSpan elapsed)
        => elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
            : $"{elapsed.TotalSeconds:0.0}秒";

    private void OnStateChanged(object? sender, DebugSessionState state)
        => Dispatch(() =>
        {
            if (state == DebugSessionState.Running && _sessionClock is not null)
            {
                _reachedRunning = true;
                IdeQualityDiagnosticLog.Write("debug.adapter-ready",
                    $"session={SessionId} elapsedMs={_sessionClock.ElapsedMilliseconds}");
            }
            IsBusy = state is DebugSessionState.Launching or DebugSessionState.Running or DebugSessionState.Stopped;
            IsStopped = state is DebugSessionState.Stopped;
            if (state is DebugSessionState.Idle or DebugSessionState.Terminated or DebugSessionState.Failed)
            {
                ExecutionLineChanged?.Invoke(null, -1);
                Inspection.Clear();
            }
            StatusMessage = state switch
            {
                DebugSessionState.Idle => "待機中",
                DebugSessionState.Launching => "起動中…",
                DebugSessionState.Running => "実行中",
                DebugSessionState.Stopped => "停止中（ブレークポイント）",
                DebugSessionState.Terminated => "終了",
                DebugSessionState.Failed => _reachedRunning
                    ? "adapter切断 — adapterを再起動して再試行してください。"
                    : "起動失敗 — 構成とadapterの導入状況を確認して再試行してください。",
                _ => StatusMessage,
            };
        });

    /// <summary>DAP イベントは UI 以外のスレッドから届くので、UI スレッドへ載せる。</summary>
    private void Dispatch(Action action) => _dispatcher.InvokeAsync(action);

    private void Dispatch(Func<Task> action) => _dispatcher.InvokeAsync(action);

    public void Dispose()
    {
        DebugService.Output -= OnOutput;
        DebugService.StateChanged -= OnStateChanged;
        DebugService.Stopped -= OnStopped;
        DebugService.Continued -= OnContinued;
        DebugService.Exited -= OnDebugExited;
        _cts?.Dispose();
    }
}
