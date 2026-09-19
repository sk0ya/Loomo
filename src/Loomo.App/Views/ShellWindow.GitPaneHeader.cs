using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Git ペインのヘッダーが幅に負けないようにする。
///
/// <para>ヘッダーには左からブランチ切替・検索・作者・期間・解除が固定幅で並び、右には詳細トグルと
/// 非表示ボタンが居る。合わせて 900px 近く要るので、ペインを縦に割ると<b>右の方から黙って切れて
/// いた</b>（DockPanel は入りきらない子を切る）。効いている絞り込みごと画面から消えるので、
/// 「一覧が絞られているのに理由がどこにも無い」状態になりうる。</para>
///
/// <para>狭いときは絞り込みのまとまり（GitLogFilterGroup）をヘッダーから<b>下の帯へ移す</b>。
/// 複製ではなく移動なので、入力中の語も期間ポップアップの配線もそのまま持っていける。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>ヘッダーに絞り込み群が並びきらなくなる幅。実測の必要幅（≈900px）より<b>狭く取らない</b>
    /// ——狭く取ると、その隙間の幅では畳まれないまま右が切れて元の不具合が残る。</summary>
    private const double GitHeaderCompactWidth = 900;

    /// <summary>畳んだ状態から戻す幅。戻す側を広めに取って、境界幅での往復（レイアウトの跳ね）を止める。</summary>
    private const double GitHeaderExpandWidth = 960;

    private bool _gitHeaderCompact;
    private bool _gitHeaderApplied;

    /// <summary>
    /// 絞り込み群を帯へ移すか。<b>今どちらで居るか</b>で閾値を変える（ヒステリシス）——1つの閾値だと、
    /// ちょうどその幅でドラッグを止めたときに移動と復帰を繰り返して震える。
    /// </summary>
    public static bool IsCompactGitHeader(double width, bool wasCompact) =>
        wasCompact ? width < GitHeaderExpandWidth : width < GitHeaderCompactWidth;

    /// <summary>帯を出すか。畳んでいるときだけの話で、<b>効いている絞り込みがある間は閉じさせない</b>
    /// ——一覧が絞られている理由は必ず画面に残す。リポジトリでないフォルダーでは絞る対象が無いので
    /// 帯もトグルも出さない（ヘッダーの絞り込み群がそもそも隠れているのと揃える）。</summary>
    public static bool ShouldShowGitFilterBand(
        bool compact, bool toggled, bool hasActiveFilters, bool isRepository) =>
        compact && isRepository && (toggled || hasActiveFilters);

    private void InitializeGitPaneHeader() {
        GitPane.SizeChanged += (_, e) => ApplyGitHeaderLayout(e.NewSize.Width);
        // 絞り込みが効き始めた／解除されたら帯の出し入れを見直す（畳んでいる間は帯が唯一の入口）。
        // リポジトリでなくなったときも同じ（絞る対象が消えるので帯ごと引っ込める）。
        _vm.GitSession.History.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(GitHistoryViewModel.HasActiveFilters))
                Dispatcher.BeginInvoke(new Action(UpdateGitFilterBand));
        };
        _vm.GitSession.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(GitSessionViewModel.IsRepository))
                Dispatcher.BeginInvoke(new Action(UpdateGitFilterBand));
        };
    }

    private void OnGitLogFilterToggled(object sender, RoutedEventArgs e) => UpdateGitFilterBand();

    /// <summary>
    /// 絞り込み欄を触れる状態にする（一覧の "/" から呼ぶ）。畳んでいるときの入力欄は
    /// <b>閉じた帯の中に居る</b>ので、開いてレイアウトを済ませないと Focus が空振りする
    /// ——キーだけ飲み込まれて何も起きない、が "/" の実際の見え方だった。
    /// </summary>
    private void RevealGitLogFilter() {
        if (!_gitHeaderCompact) return;
        GitLogFilterToggle.IsChecked = true;
        UpdateGitFilterBand();
        GitLogFilterBand.UpdateLayout();
    }

    private void ApplyGitHeaderLayout(double width) {
        var compact = IsCompactGitHeader(width, _gitHeaderCompact);
        if (_gitHeaderApplied && compact == _gitHeaderCompact) return;
        _gitHeaderApplied = true;
        _gitHeaderCompact = compact;

        // WPF の要素は親を1つしか持てないので、移す前に必ず今の親から外す。
        if (compact) {
            GitPaneHeaderFilters.Children.Remove(GitLogFilterGroup);
            GitLogFilterBand.Child = GitLogFilterGroup;
        } else {
            GitLogFilterBand.Child = null;
            if (!GitPaneHeaderFilters.Children.Contains(GitLogFilterGroup))
                GitPaneHeaderFilters.Children.Add(GitLogFilterGroup);
        }
        UpdateGitFilterBand();
    }

    private void UpdateGitFilterBand() {
        var isRepository = _vm.GitSession.IsRepository;
        var hasActiveFilters = _vm.GitSession.History.HasActiveFilters;
        var show = ShouldShowGitFilterBand(_gitHeaderCompact,
            GitLogFilterToggle.IsChecked == true, hasActiveFilters, isRepository);
        GitLogFilterBand.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GitLogFilterToggle.Visibility =
            _gitHeaderCompact && isRepository ? Visibility.Visible : Visibility.Collapsed;

        // 絞り込みが効いている間は畳めない。そのときトグルを押せるままにすると、押しても帯が
        // 閉じない＝壊れたボタンになるので、ON で固定したうえで理由を出して無効にする
        // （IsChecked の代入は Checked→ここ、と一度だけ戻ってくるが、同じ値なので止まる）。
        var locked = show && hasActiveFilters;
        if (locked && GitLogFilterToggle.IsChecked != true)
            GitLogFilterToggle.IsChecked = true;
        GitLogFilterToggle.IsEnabled = !locked;
        GitLogFilterToggle.ToolTip = locked
            ? "絞り込みが効いている間は畳めません（「✕ 解除」で戻せます）"
            : "コミットの絞り込みを表示／非表示";
    }
}
