namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 差分の<b>行き先</b>を決める一箇所（設計書 §24.5.2）。「Diff へ送る」「差分を開く」
/// の入口は Git・エディタ・ターミナル・エクスプローラーと部屋のあちこちにあるが、送られてきた
/// <see cref="DiffOpenTarget"/> をどこへ出すかの判断はここだけが持つ。
///
/// <para>Diff ペインが出ていればそのペインへ。<b>隠れているときは別ウィンドウで開く</b>——差分を
/// 見たいだけの一瞬のために、そこに置いてあったペイン（ターミナルやエディタ）を追い出して部屋の
/// 配置を崩すのは対価が大きい。窓なら見終わって閉じれば元の配置がそのまま残る。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>差分を見せる。ペインが出ていなければ別ウィンドウで開く（このクラスの主役）。</summary>
    private void ShowDiff(DiffOpenTarget target) {
        // ステージモードでは「隠れている」ペインは無い（袖に居るだけで、舞台へ上げれば出る）ので
        // 従来どおり舞台へ出す。窓へ逃がすのは、ペインが配置から消えているときだけ。
        if (_stageActive || IsPaneVisible(PaneKind.Diff)) {
            _ = _vm.DiffSession.ShowAsync(target);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
            FocusPane(PaneKind.Diff);
            return;
        }
        ShowDiffInDetachedWindow(target);
    }

    /// <summary>差分をペイン外の窓で開く。<b>切り離しウィンドウが既に出ていれば、そこのタブとして足す</b>
    /// ——差分は溜めて見比べる物なので前のを上書きはしないが、送るたびに窓が増えると画面が埋まる。
    /// 足す先は直近に前へ出た窓で、相手が差分の窓かどうかは問わない（エディタを切り離した窓でも同じ
    /// ——「いま開いている別ウィンドウ」がタブの行き先という一本の約束にする）。並べて見比べたくなったら
    /// タブを掴んで外へ落とせば別窓になる（切り離しウィンドウの既存の作法）。</summary>
    private void ShowDiffInDetachedWindow(DiffOpenTarget target) {
        var item = CreateDiffSpinoffItem(target);
        // 窓が1つも出ていなければ（TryAddToRecentWindow が false）新しい窓を開く。
        if (!Detached.TryAddToRecentWindow(item))
            Detached.Detach(item);
    }

    /// <summary>切り離した窓の VM へ差分を出す。作業ツリーの差分だけは<b>追従させる</b>
    /// （ペインと同じで、ステージや編集のたびに窓の中身が古くなるため）。コミットの差分は動かない。</summary>
    private static Task ShowDiffInWindowAsync(DiffSessionViewModel vm, DiffOpenTarget target) {
        if (target is DiffOpenTarget.WorkingTreeFile)
            vm.StartLiveTracking();
        return vm.ShowAsync(target);
    }
}
