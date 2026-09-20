namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 差分の<b>行き先</b>を決める一箇所（設計書 §24.5.2）。「Diff へ送る」「差分を開く」
/// の入口は Git・エディタ・ターミナル・エクスプローラーと部屋のあちこちにあるが、送られてきた
/// <see cref="DiffOpenTarget"/> をどこへ出すかの判断はここだけが持つ。
///
/// <para>Diff ペインが出ていればそのペインへ。<b>隠れているときは別ウィンドウで開く</b>——差分を
/// 見たいだけの一瞬のために、そこに置いてあったペイン（ターミナルやエディタ）を追い出して部屋の
/// 配置を崩すのは対価が大きい。窓なら見終わって閉じれば元の配置がそのまま残る。
/// 集中（袖）では「隠れている」ペインが無いのでそのまま舞台へ出す。
/// <b>ドックでは Diff をペインに置かず、常に別ウィンドウ</b>——ドックの中央は1枚なので、差分を
/// 出すと書いていたエディタが押し出される（<see cref="DockLayoutCoordinator.DockOrder"/> にも居ない）。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>差分を見せる。ペインが出ていなければ別ウィンドウで開く（このクラスの主役）。</summary>
    private void ShowDiff(DiffOpenTarget target) {
        // 集中では袖に居るだけなので、そのまま舞台へ出す
        // （EnsurePaneVisibleOrSwapTopLeft がモードごとの出し方を持っている）。
        // 窓へ逃がすのは、ドック中か、分割でペインが配置から消えているとき。
        if (!_dockActive && IsPaneMaterialized(PaneKind.Diff)) {
            _ = _vm.DiffSession.ShowAsync(target);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
            FocusPane(PaneKind.Diff);
            return;
        }
        ShowDiffInDetachedWindow(target);
    }

    /// <summary>差分をペイン外の窓で開く。行き先は他の切り離しと同じ <see cref="DetachedWindowManager.Detach"/>
    /// ——切り離しウィンドウが既に出ていればそこのタブとして足す。差分は溜めて見比べる物なので前のを
    /// 上書きはしないが、送るたびに窓が増えると画面が埋まる。</summary>
    private void ShowDiffInDetachedWindow(DiffOpenTarget target)
        => Detached.Detach(CreateDiffSpinoffItem(target));

    /// <summary>切り離した窓の VM へ差分を出す。作業ツリーの差分だけは<b>追従させる</b>
    /// （ペインと同じで、ステージや編集のたびに窓の中身が古くなるため）。コミットの差分は動かない。</summary>
    private static Task ShowDiffInWindowAsync(DiffSessionViewModel vm, DiffOpenTarget target) {
        if (target is DiffOpenTarget.WorkingTreeFile)
            vm.StartLiveTracking();
        return vm.ShowAsync(target);
    }
}
