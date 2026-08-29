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
    /// <summary>ペインが隠れているときに開いた差分のタブたち（新しいものが末尾）。次の差分は
    /// <b>最後のタブと同じ窓へ足す</b>——差分は溜めて見比べる物なので上書きはしないが、送るたびに
    /// 窓が増えると画面が埋まる。窓は1つ・タブが増える形にして、並べたくなったらタブを引き出せばよい
    /// （切り離しウィンドウのタブは掴んで外へ落とすと別窓になる）。閉じられたタブはここから外れる。</summary>
    private readonly List<DetachedItem> _diffTabs = new();

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

    /// <summary>差分をペイン外の窓で開く。既に開いている差分タブがあれば<b>その窓のタブとして</b>足す。</summary>
    private void ShowDiffInDetachedWindow(DiffOpenTarget target) {
        var sibling = _diffTabs.Count > 0 ? _diffTabs[^1] : null;   // 足す先は最後に開いたタブの窓
        DetachedItem? item = null;
        item = CreateDiffSpinoffItem(target, onDisposed: () => {
            if (item is { } closed) _diffTabs.Remove(closed);
        });
        _diffTabs.Add(item);
        // 相手の窓が既に閉じられていれば（TryAddNextTo が false）新しい窓を開く。
        if (sibling is null || !Detached.TryAddNextTo(sibling, item))
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
