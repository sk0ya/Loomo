namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: デバッグの準備（ブレークポイントとウォッチ式）を部屋に残す（§28.16・RAD Debugger 0.9.29 の
/// プロジェクトごとのウォッチ）。部屋を開くと・切り替えると、その部屋のものに入れ替わる。</summary>
public partial class ShellWindow
{
    private DebugRoomSnapshot CaptureDebugState() => new()
    {
        Dotnet = CaptureDebugManager(_vm.Debug),
        TypeScript = CaptureDebugManager(_vm.TsIde),
    };

    private static DebugManagerSnapshot CaptureDebugManager(DebugManagerViewModelBase manager) => new()
    {
        Breakpoints = manager.Breakpoints.Capture(),
        Watches = manager.WatchExpressions.ToList(),
    };

    /// <summary>部屋のブレークポイントとウォッチ式へ入れ替える。保存したことの無い部屋（null）は空にする——
    /// 前の部屋のブレークポイントを別の部屋へ持ち込まない。</summary>
    private void RestoreDebugState(DebugRoomSnapshot? state) {
        RestoreDebugManager(_vm.Debug, state?.Dotnet);
        RestoreDebugManager(_vm.TsIde, state?.TypeScript);
    }

    private static void RestoreDebugManager(DebugManagerViewModelBase manager, DebugManagerSnapshot? state) {
        manager.Breakpoints.Load(state?.Breakpoints);
        manager.LoadWatchExpressions(state?.Watches);
    }
}
