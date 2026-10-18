using System.Windows.Threading;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: IDE ペインの「モニター」タブ（§28.15）の時計。IDE のセッションが走っている（または止まっている）間だけ
/// 1 秒ごとに、いま選んでいるセッションのデバッグ対象を読む。メモリが増え続けたら通知欄へ出す（ペインを見ていなくても気づけるように）。</summary>
public partial class ShellWindow {
    private DispatcherTimer? _debugMonitorTimer;

    private void InitializeDebugMonitor() {
        var manager = _vm.Debug;
        manager.Monitor.GrowthDetected += message => NotificationService.Post(
            message, NotificationKind.Warning, "IDE · モニター",
            new NotificationAction("モニターを見る", () => ShowPaneForRunning(PaneKind.Debug)));
        manager.SessionStateChanged += UpdateDebugMonitorTimer;
        manager.Sessions.CollectionChanged += (_, _) => UpdateDebugMonitorTimer();
    }

    private void UpdateDebugMonitorTimer() {
        var running = _vm.Debug.Sessions.Any(s => s.IsBusy || s.IsStopped);
        if (running) {
            if (_debugMonitorTimer is null) {
                _debugMonitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                _debugMonitorTimer.Tick += (_, _) => TickDebugMonitor();
            }
            if (!_debugMonitorTimer.IsEnabled) {
                TickDebugMonitor();
                _debugMonitorTimer.Start();
            }
            return;
        }
        if (_debugMonitorTimer is { IsEnabled: true }) {
            _debugMonitorTimer.Stop();
            _vm.Debug.Monitor.Tick(null, false, DateTime.UtcNow);   // 「終了」の印を付けて締める
        }
    }

    private void TickDebugMonitor() {
        var session = _vm.Debug.ActiveSession is { } active && (active.IsBusy || active.IsStopped)
            ? active
            : _vm.Debug.Sessions.FirstOrDefault(s => s.IsBusy || s.IsStopped);
        _vm.Debug.Monitor.Tick(session?.DebugService.DebuggeeProcessId, session?.IsStopped == true, DateTime.UtcNow);
    }
}
