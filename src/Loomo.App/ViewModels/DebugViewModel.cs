using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグコンソールの 1 行。<see cref="Category"/> で色分けする（XAML 側のトリガ）。</summary>
public sealed class DebugOutputLine
{
    public DebugOutputLine(DebugOutputCategory category, string text)
    {
        Category = category;
        Text = text;
    }

    public DebugOutputCategory Category { get; }
    public string Text { get; }
}

/// <summary>
/// IDE（デバッグ）ペインのファサード ViewModel。セッション状態（実行中/停止中/タスク実行中・状態文言・アダプタ有無）と
/// コンソール出力を一元的に持ち、機能ごとのサブ ViewModel（<see cref="Launch"/>/<see cref="Tests"/>/<see cref="Inspection"/>/
/// <see cref="Breakpoints"/>/<see cref="Attach"/>）を束ねる。デバッグアダプタ（DAP）のイベントを受けて状態を更新し、
/// エディタ連携（実行行・プレビュー・ガター同期）の通知を中継する。
/// </summary>
public sealed partial class DebugViewModel : ObservableObject, IDebugSession, IDisposable
{
    private readonly IDebugService _debug;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    /// <summary>状態の短い説明（ヘッダ脇に表示）。</summary>
    [ObservableProperty] private string _statusMessage = "待機中";

    /// <summary>セッションが起動中/実行中か（開始ボタンの可否・停止ボタンの表示に使う）。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>デバッグアダプタ（netcoredbg）が未導入か（導入を促すバーの表示に使う）。</summary>
    [ObservableProperty] private bool _isAdapterMissing;

    /// <summary>ブレークポイント等で停止中か（ステップ/続行ボタンの表示・可否に使う）。</summary>
    [ObservableProperty] private bool _isStopped;

    /// <summary>ビルド/テストを実行中か（デバッグの <see cref="IsBusy"/> とは別系統。同時実行を防ぐゲート）。</summary>
    [ObservableProperty] private bool _isTaskRunning;

    /// <summary>コンソール出力。</summary>
    public ObservableCollection<DebugOutputLine> Output { get; } = new();

    // --- サブ ViewModel（機能ごと） ---
    public DebugBreakpointsViewModel Breakpoints { get; }
    public DebugInspectionViewModel Inspection { get; }
    public DebugAttachViewModel Attach { get; }
    public DebugTestsViewModel Tests { get; }
    public DebugLaunchViewModel Launch { get; }
    public DebugProfilesViewModel Profiles { get; }

    // --- エディタ連携の通知（ShellWindow が購読） ---

    /// <summary>停止位置をエディタへ反映するための通知（path, 0始まり行）。行が -1 のときは実行行ハイライト解除。
    /// path が null のときは全エディタの実行行を解除する。</summary>
    public event Action<string?, int>? ExecutionLineChanged;

    /// <summary>停止/実行の切り替わり通知（true=停止）。エディタの DataTip（ホバー値表示）の有効化に使う。</summary>
    public event Action<bool>? StoppedChanged;

    /// <summary>コールスタックのフレーム選択でソースをプレビュー表示する要求（path, 0始まり行）。</summary>
    public event Action<string, int>? FramePreviewRequested;

    /// <summary>ソースへジャンプする要求（path, 0始まり行）。通常タブで開き、エディタにフォーカスする。</summary>
    public event Action<string, int>? FrameActivated;

    /// <summary>ブレークポイント一覧が（パネル操作で）変わったので、そのパスのエディタのガターを同期し直す要求。</summary>
    public event Action<string>? BreakpointsRefreshed;

    /// <summary>実行系コマンド（開始/アタッチ/ビルド/テスト）を押した瞬間に「出力」タブを見せる要求。</summary>
    public event Action? OutputRequested;

    public DebugViewModel(IDebugService debug, IWorkspaceService workspace, ITerminalService terminal,
        ITestDiscoveryService testDiscovery, DebugLaunchProfileStore profileStore)
    {
        _debug = debug;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Breakpoints = new DebugBreakpointsViewModel(debug, this);
        Inspection = new DebugInspectionViewModel(debug, this);
        Attach = new DebugAttachViewModel(debug, this);
        Tests = new DebugTestsViewModel(workspace, terminal, testDiscovery, this);
        Profiles = new DebugProfilesViewModel(workspace, profileStore);
        Launch = new DebugLaunchViewModel(debug, workspace, terminal, this, Inspection, Attach, Profiles);
        Profiles.AttachLaunch(Launch);
        _findBuildTarget = () => DebugTargetResolver.FindBuildTarget(workspace, this);

        _debug.Output += OnOutput;
        _debug.StateChanged += OnStateChanged;
        _debug.Stopped += OnStopped;
        _debug.Continued += OnContinued;
        _debug.Exited += OnDebugExited;

        IsAdapterMissing = !_debug.IsAdapterAvailable;
    }

    private readonly Func<string?> _findBuildTarget;

    /// <summary>パネルが開かれたときにアダプタ導入状況を取り直す（導入直後でも反映されるように）。</summary>
    public void Refresh() => IsAdapterMissing = !_debug.IsAdapterAvailable;

    // --- IDebugSession 実装（サブ VM 共有の窓口） ---

    bool IDebugSession.IsBusy => IsBusy;
    bool IDebugSession.IsStopped => IsStopped;
    bool IDebugSession.IsAdapterMissing => IsAdapterMissing;

    public event Action? SessionStateChanged;

    partial void OnIsBusyChanged(bool value) => SessionStateChanged?.Invoke();
    partial void OnIsTaskRunningChanged(bool value) => SessionStateChanged?.Invoke();
    partial void OnIsStoppedChanged(bool value)
    {
        StoppedChanged?.Invoke(value);
        SessionStateChanged?.Invoke();
    }

    void IDebugSession.RefreshAdapter() => Refresh();

    CancellationToken IDebugSession.BeginSession()
    {
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    void IDebugSession.CancelSession() => _cts?.Cancel();

    void IDebugSession.RequestOutput() => OutputRequested?.Invoke();
    void IDebugSession.RaiseExecutionLine(string? path, int line0) => ExecutionLineChanged?.Invoke(path, line0);
    void IDebugSession.RaiseFramePreview(string path, int line0) => FramePreviewRequested?.Invoke(path, line0);
    void IDebugSession.RaiseFrameActivated(string path, int line0) => FrameActivated?.Invoke(path, line0);
    void IDebugSession.RaiseBreakpointsRefreshed(string path) => BreakpointsRefreshed?.Invoke(path);
    string? IDebugSession.FindBuildTarget() => _findBuildTarget();

    // --- コンソール出力 ---

    void IDebugSession.Append(DebugOutputCategory category, string text) => Append(category, text);

    private void Append(DebugOutputCategory category, string text)
    {
        Output.Add(new DebugOutputLine(category, text));
        const int max = 2000;
        if (Output.Count > max) Output.RemoveAt(0);
    }

    /// <summary>複数行のコマンド出力を 1 行ずつコンソールへ流す（末尾の CR を落とし、空行は捨てる）。</summary>
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
            if (!string.IsNullOrEmpty(e.SourcePath) && e.Line > 0)
            {
                Append(DebugOutputCategory.Important,
                    $"停止（{e.Reason}）: {Path.GetFileName(e.SourcePath)}:{e.Line}");
                ExecutionLineChanged?.Invoke(e.SourcePath, e.Line - 1);  // DAP 1始まり → エディタ 0始まり
            }
            else
            {
                Append(DebugOutputCategory.Important, $"停止（{e.Reason}）");
            }
            await Inspection.OnStoppedAsync();
        });

    private void OnContinued(object? sender, EventArgs e)
        => Dispatch(() => { ExecutionLineChanged?.Invoke(null, -1); Inspection.Clear(); });

    private void OnDebugExited(object? sender, DebugExited e)
        => Dispatch(() => { ExecutionLineChanged?.Invoke(null, -1); Inspection.Clear(); });

    private void OnStateChanged(object? sender, DebugSessionState state)
        => Dispatch(() =>
        {
            IsBusy = state is DebugSessionState.Launching or DebugSessionState.Running or DebugSessionState.Stopped;
            IsStopped = state is DebugSessionState.Stopped;
            // セッション終了（手動停止＝Idle 含む）では Exited イベントが届かないことがあるので、
            // ここで実行行ハイライトと検査ペインを確実に片付ける。
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
                DebugSessionState.Failed => "失敗",
                _ => StatusMessage,
            };
        });

    /// <summary>DAP イベントは UI 以外のスレッドから届くので、UI スレッドへ載せる。</summary>
    private void Dispatch(Action action) => _dispatcher.InvokeAsync(action);

    private void Dispatch(Func<System.Threading.Tasks.Task> action) => _dispatcher.InvokeAsync(action);

    public void Dispose()
    {
        Tests.Dispose();
        Profiles.Dispose();
    }
}
