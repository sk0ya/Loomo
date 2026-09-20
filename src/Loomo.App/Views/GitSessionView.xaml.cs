
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Git セッションペイン。コミットグラフ・ブランチ一覧の表示と、コンテキストメニューからの
/// 複雑な git 操作（rebase / merge / cherry-pick / reset 等）を受け付ける。
/// 名前入力・破壊的操作の確認ダイアログはここ（ビュー）が担い、git 実行は ViewModel に委ねる。
/// </summary>
public partial class GitSessionView : UserControl
{
    private GitHistoryViewModel? _subscribed;
    private GitSessionViewModel? _subscribedSession;
    private bool _isRevealingLogRow;
    private System.Windows.Threading.DispatcherTimer? _branchMenuTimer;
    private TreeViewItem? _pendingBranchMenuItem;
    private GitBranchInfo? _branchMenuTarget;
    private int _branchLogRequest;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>コミット詳細を隠す直前の幅。再表示でユーザーがドラッグした幅へ戻すため覚えておく。</summary>
    private GridLength _commitDetailWidth = new(300);

    /// <summary>ブランチ一覧の列を隠す直前の幅。再表示でユーザーがドラッグした幅へ戻すため覚えておく。</summary>
    private GridLength _branchColumnWidth = new(190);

    /// <summary>畳んだときに残す左列の幅（＝操作バーの設計値）。実測が取れるならそちらを使う。</summary>
    private const double CollapsedBranchColumnWidth = 29;

    /// <summary>直前に画面へ反映した左列の開閉（null＝まだ一度も反映していない）。
    /// 「畳む直前の幅を覚える」を<b>開いていたときだけ</b>に限るための控え——畳んだ列の実寸は
    /// 操作バーの幅（約 29px）なので、畳んだまま二度目の反映が来たときに測ると、ユーザーが
    /// ドラッグした幅がその 29px で潰れる。</summary>
    private bool? _branchColumnApplied;

    public GitSessionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SetupLogColumnResize();
        // 無選択で立ち上がるので、選んだ1本に効く操作は最初から押せない状態にしておく
        UpdateBranchOpButtons();
        if (BranchList.ContextMenu is { } menu)
            menu.Closed += (_, _) => _branchMenuTarget = null;
    }

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;

    // ===== コミット詳細（変更ファイル一覧）のリンク描画 =====

    /// <summary>DataContext（VM）の差し替えに追従し、CommitDetail の変化を購読し直す。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = Vm?.History;
        if (_subscribed is not null)
            _subscribed.PropertyChanged += OnVmPropertyChanged;

        if (_subscribedSession is not null)
            _subscribedSession.PropertyChanged -= OnSessionPropertyChanged;
        _subscribedSession = Vm;
        if (_subscribedSession is not null)
            _subscribedSession.PropertyChanged += OnSessionPropertyChanged;

        ApplyCommitDetailVisibility();
        ApplyBranchColumnVisibility();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitSessionViewModel.CommitDetailVisible))
            ApplyCommitDetailVisibility();
        else if (e.PropertyName == nameof(GitSessionViewModel.BranchColumnVisible))
            ApplyBranchColumnVisibility();
    }

    /// <summary>
    /// 左列（ブランチ一覧＋タグ／リモート／サブモジュール）の表示/非表示を反映する。畳むのは
    /// <b>一覧だけ</b>で、左端の操作バーは残す——開閉ボタンがそのバーに居るので、列ごと消すと
    /// 戻す入口が画面から無くなる。列は幅と MinWidth をバーの実寸まで詰める（中身を Collapsed に
    /// するだけでは列の固定幅が残り、空白の帯が居座るため）。
    ///
    /// <para>VM が無い間は<b>何もしない</b>——ペインの付け替え（RebuildPaneLayout）で DataContext は
    /// 一度外れて戻る。そこで「表示」を既定にして反映すると、畳んだ状態が一瞬だけ開いた扱いになり、
    /// 戻ってきた拍子に覚えている幅を畳んだ幅で潰してしまう。</para>
    /// </summary>
    private void ApplyBranchColumnVisibility()
    {
        if (Vm is not { } vm)
            return;

        var visible = vm.BranchColumnVisible;
        if (visible)
        {
            BranchSplitterColumn.Width = new GridLength(6);
            BranchColumn.MinWidth = 120;
            BranchColumn.Width = _branchColumnWidth;
            BranchSplitter.Visibility = Visibility.Visible;
            BranchListArea.Visibility = Visibility.Visible;
            BranchOpListGroup.Visibility = Visibility.Visible;
        }
        else
        {
            // ドラッグ後の実寸を覚えてから畳む（次の表示で同じ幅に戻す）。測るのは開いていたとき
            // だけ——畳んだ列の実寸は操作バーの幅なので、畳んだまま測ると覚えた幅がそれで潰れる。
            if (_branchColumnApplied != false && BranchColumn.ActualWidth > 0)
                _branchColumnWidth = new GridLength(BranchColumn.ActualWidth);
            BranchSplitter.Visibility = Visibility.Collapsed;
            BranchListArea.Visibility = Visibility.Collapsed;
            BranchOpListGroup.Visibility = Visibility.Collapsed;
            BranchSplitterColumn.Width = new GridLength(0);
            // バーの実寸へ詰める。まだ測っていない（起動直後に畳んだ状態で復元した）ときだけ
            // 設計値へ落とす＝ボタン 26px ＋ Padding 1×2 ＋ 右の区切り線 1px。
            var barWidth = BranchOpBar.ActualWidth > 0 ? BranchOpBar.ActualWidth : CollapsedBranchColumnWidth;
            BranchColumn.MinWidth = barWidth;
            BranchColumn.Width = new GridLength(barWidth);
        }

        _branchColumnApplied = visible;
    }

    /// <summary>
    /// コミット詳細（グラフの右の列）の表示/非表示を反映する。非表示のときは列の幅と MinWidth ごと
    /// 0 にして畳む（中身を Collapsed にするだけでは列の固定幅が残り、空白の帯が居座るため）。
    /// </summary>
    private void ApplyCommitDetailVisibility()
    {
        var visible = Vm?.CommitDetailVisible ?? true;
        if (visible)
        {
            CommitDetailSplitterColumn.Width = new GridLength(6);
            CommitDetailColumn.MinWidth = 140;
            CommitDetailColumn.Width = _commitDetailWidth;
            CommitDetailSplitter.Visibility = Visibility.Visible;
            CommitDetailPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // ドラッグ後の実寸を覚えてから畳む（次の表示で同じ幅に戻す）
            if (CommitDetailColumn.ActualWidth > 0)
                _commitDetailWidth = new GridLength(CommitDetailColumn.ActualWidth);
            CommitDetailSplitter.Visibility = Visibility.Collapsed;
            CommitDetailPanel.Visibility = Visibility.Collapsed;
            CommitDetailSplitterColumn.Width = new GridLength(0);
            CommitDetailColumn.MinWidth = 0;
            CommitDetailColumn.Width = new GridLength(0);
        }
    }

    /// <summary>
    /// 左列下段の種類切替（タグ／リモート／サブモジュール）。
    ///
    /// <para><b>選択中のボタンを押しても外れないようにする</b>のがここの主な仕事。ToggleButton は
    /// Click が届く前に自分で IsChecked を反転させてしまうので、同じ種類を押し直すと
    /// 「ボタンは OFF なのに一覧はその種類のまま」になる——VM の値は変わらず、通知も飛ばないので
    /// 片方向バインディングは二度と true を押し戻さない。押されたボタンだけ結び直して実体へ揃える
    /// （種類が実際に変わったときは通知で3つとも揃うので、これで足りる）。</para>
    /// </summary>
    private void OnReferenceTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement { Tag: string tag } button) return;
        if (Enum.TryParse<GitReferenceTab>(tag, out var tab))
            vm.ReferenceTab = tab;
        (button as ToggleButton)?.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    /// <summary>操作バーのいちばん上の開閉ボタン。VM 側を反転させ、
    /// 表示の反映（列を畳む／戻す）と永続化はそちらの変更通知経由で行う。</summary>
    private void OnBranchColumnToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            vm.BranchColumnVisible = !vm.BranchColumnVisible;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitHistoryViewModel.SelectedLogRow) &&
            Vm?.History.SelectedLogRow is { } row && !_isRevealingLogRow)
        {
            // Extended 選択の ListView は SelectedItem バインディングだけでは外部からの選択が
            // コンテナへ反映されない場合があるため、実体の選択も明示して画面内へ出す。
            Dispatcher.BeginInvoke(() => SelectAndRevealLogRow(row));
        }
    }

    private void SelectAndRevealLogRow(GitLogRow row)
    {
        if (!LogList.Items.Contains(row))
            return;
        _isRevealingLogRow = true;
        try
        {
            // バインディングが既に選択を反映していれば触らない。Clear→再選択は VM の変更通知を
            // 再発火させ、Dispatcher に同じ処理を積み続けるため行わない。
            if (!ReferenceEquals(LogList.SelectedItem, row))
                LogList.SelectedItem = row;
            LogList.ScrollIntoView(row);
            LogList.UpdateLayout();
            if (LogList.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem item)
                item.BringIntoView();
        }
        finally
        {
            _isRevealingLogRow = false;
        }
    }

    /// <summary>変更ファイル一覧で選択中のファイルノード（フォルダ行なら null）。</summary>
    private CommitFileNode? SelectedCommitFile =>
        CommitFileList.SelectedItem is CommitFileNode { IsDirectory: false } node ? node : null;

    /// <summary>
    /// ダブルクリックは<b>そのファイルの差分を別ウィンドウ</b>で開く（フォルダ行は開閉に任せて何もしない）。
    /// 一覧の主目的は「このコミットで何がどう変わったか」を見ることなので、いまの中身を開く
    /// （＝名前のリンククリック）より差分の方が近い。ペインの差分表示は奪わない。
    /// </summary>
    private void OnCommitFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && SelectedCommitFile?.NavigatePath is { } path)
        {
            e.Handled = true;
            vm.RequestChangedFileDiffWindow(path);
        }
    }

    private void OnCommitFileDiffWindow(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommitFile?.NavigatePath is { } path)
            vm.RequestChangedFileDiffWindow(path);
    }

    private async void OnCommitFileOpen(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommitFile?.NavigatePath is { } path)
            await vm.OpenChangedFileAsync(path);
    }

    /// <summary>
    /// 変更ファイル一覧のコンテキストメニューは<b>ファイル行の上でだけ</b>出す。フォルダ行には
    /// 「開く／コピー」の対象が無く、行の外（空白域）で出すと直前に選んでいた<b>別の</b>ファイルへ
    /// 操作が効いてしまう（右クリックは行が無ければ選択を動かさないため）。
    /// キーボード起動（CursorLeft が負）は選択行そのものが対象なので、位置は問わない。
    /// </summary>
    private void OnCommitFileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var onRow = e.CursorLeft < 0 || FindRowContainer(e.OriginalSource) is not null;
        if (!onRow || SelectedCommitFile is null)
            e.Handled = true;
    }

    private void OnCommitFileCopyPath(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedCommitFile?.NavigatePath);

    /// <summary>右クリックでも対象行を選択状態にする（コンテキストメニューの対象を確定させる）。</summary>
    private void OnListRightClickSelect(object sender, MouseButtonEventArgs e)
    {
        var container = FindRowContainer(e.OriginalSource);
        if (container is ListBoxItem listItem)
            listItem.IsSelected = true;
        else if (container is TreeViewItem treeItem)
            treeItem.IsSelected = true;
    }

    /// <summary>クリック元から一覧の行コンテナを遡って探す。行の外（空白域）なら null。</summary>
    private static DependencyObject? FindRowContainer(object? originalSource)
    {
        var element = originalSource as DependencyObject;
        // OriginalSource が Run 等の FrameworkContentElement のことがある（VisualTreeHelper だと例外）
        while (element is not null and not ListBoxItem and not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element;
    }

    // ===== ブランチ操作 =====

    /// <summary>ツリーで選択中のブランチ。フォルダノード選択中は null（各操作は何もしない）。</summary>
    private GitBranchInfo? SelectedTreeBranch => (BranchList.SelectedItem as BranchTreeNode)?.Branch;
    private GitBranchInfo? SelectedBranch => _branchMenuTarget ?? SelectedTreeBranch;

    /// <summary>
    /// ブランチのダブルクリックはチェックアウトではなく、右側のコミットグラフをそのブランチに切り替える
    /// （ブランチの切り替え自体はヘッダーのブランチ切替コントロールから行う）。
    /// </summary>
    private async void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        // 連打中は SelectedItem が次のクリックで変わっている可能性があるため、
        // TreeView の現在選択ではなく、ダブルクリックされた行を対象にする。
        var branch = FindRowContainer(e.OriginalSource) is TreeViewItem
            {
                DataContext: BranchTreeNode { Branch: { } clickedBranch }
            }
            ? clickedBranch
            : SelectedTreeBranch;
        if (branch is null)
            return;

        await ShowBranchLogGuardedAsync(vm, branch);
    }

    /// <summary>
    /// そのブランチのコミットを一覧に出す（ダブルクリックと Enter の共通経路）。
    /// async void のイベントハンドラーから例外を出すとアプリ全体が終了するので必ず受け、
    /// 連打で古い読み込みが失敗したときは<b>最新の操作のぶんだけ</b>を画面へ知らせる
    /// （さもないと、もう表示していないブランチのエラーが後から居座る）。
    /// </summary>
    private async Task ShowBranchLogGuardedAsync(GitSessionViewModel vm, GitBranchInfo branch)
    {
        CancelPendingBranchMenu();
        var request = ++_branchLogRequest;
        try
        {
            await vm.ShowBranchLogAsync(branch);
        }
        catch (Exception exception)
        {
            if (request == _branchLogRequest)
            {
                vm.StatusIsError = true;
                vm.StatusMessage = exception.Message;
            }
        }
    }

    /// <summary>
    /// フォルダ行はクリック一回で開閉し、リーフ＝ブランチ行は選択して操作メニューを開く。
    /// 展開矢印（ToggleButton, ClickMode=Press）上のクリックは既に開閉済みなので二重に反応しない。
    /// </summary>
    private void OnBranchTreeClick(object sender, MouseButtonEventArgs e)
    {
        // 2回目のクリックでは MouseDoubleClick のログ表示を優先する。
        if (e.ClickCount > 1)
        {
            CancelPendingBranchMenu();
            return;
        }

        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton)
                return;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (element is not TreeViewItem { DataContext: BranchTreeNode node } item)
            return;

        if (node.IsFolder)
        {
            item.IsExpanded = !item.IsExpanded;
            return;
        }

        item.IsSelected = true;
        ScheduleBranchMenu(item);
        e.Handled = true;
    }

    /// <summary>単クリックとダブルクリックを区別するため、メニュー表示を少し遅延させる。</summary>
    private void ScheduleBranchMenu(TreeViewItem item)
    {
        CancelPendingBranchMenu();
        _pendingBranchMenuItem = item;
        _branchMenuTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(GetDoubleClickTime(), 1u))
        };
        _branchMenuTimer.Tick += OnBranchMenuTimerTick;
        _branchMenuTimer.Start();
    }

    private void OnBranchMenuTimerTick(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Threading.DispatcherTimer timer)
        {
            timer.Stop();
            timer.Tick -= OnBranchMenuTimerTick;
        }

        var item = _pendingBranchMenuItem;
        _pendingBranchMenuItem = null;
        _branchMenuTimer = null;
        if (item is { IsLoaded: true, DataContext: BranchTreeNode { Branch: not null } })
            OpenBranchMenu(item);
    }

    private void CancelPendingBranchMenu()
    {
        if (_branchMenuTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnBranchMenuTimerTick;
        }

        _branchMenuTimer = null;
        _pendingBranchMenuItem = null;
    }

    /// <summary>左クリックで開くブランチメニューを、クリックされた行の下に配置する。</summary>
    private void OpenBranchMenu(TreeViewItem item)
    {
        if (BranchList.ContextMenu is not { } menu)
            return;
        if (item is not { IsLoaded: true, DataContext: BranchTreeNode { Branch: { } branch } })
            return;

        // 前のメニューが閉じる前に次のクリックが来ても、同じ Popup を再利用する。
        if (menu.IsOpen)
            menu.IsOpen = false;
        _branchMenuTarget = branch;
        if (!TryPrepareBranchMenu(branch))
            return;

        PlaceBranchMenu(menu, item);
        menu.IsOpen = true;
    }

    private static void PlaceBranchMenu(ContextMenu menu, TreeViewItem item)
    {
        menu.PlacementTarget = item;
        menu.Placement = PlacementMode.Right;
        menu.HorizontalOffset = 4;
        menu.StaysOpen = false;
    }

    private async void OnShowAllBranchesLog(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.ShowAllBranchesLogAsync();
    }

    /// <summary>
    /// 対象が無い（フォルダ・見出しを右クリックした）ならメニューごと出さない。ブランチ行なら、
    /// そのブランチに意味を成さない項目を無効化する（自分自身へのチェックアウト／マージ／リベース、
    /// 現在ブランチの削除、リモートブランチの削除＝git branch -d では消せない）。
    /// タイトルバーのブランチ切替（BranchSwitcherView.OnTreeContextMenuOpening）と同じ作法。
    /// </summary>
    private void OnBranchContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindRowContainer(e.OriginalSource) as TreeViewItem;
        _branchMenuTarget = item?.DataContext is BranchTreeNode { Branch: { } branch }
            ? branch
            : SelectedTreeBranch;
        if (!TryPrepareBranchMenu(_branchMenuTarget))
        {
            e.Handled = true;
            return;
        }

        if (item is not null && BranchList.ContextMenu is { } menu)
            PlaceBranchMenu(menu, item);
    }

    private bool TryPrepareBranchMenu(GitBranchInfo? target)
    {
        if (target is not { } branch)
            return false;

        BranchMenuCheckout.IsEnabled = !branch.IsCurrent;
        BranchMenuMerge.IsEnabled = !branch.IsCurrent;
        BranchMenuMergeStrategy.IsEnabled = !branch.IsCurrent;
        BranchMenuRebase.IsEnabled = !branch.IsCurrent;
        BranchMenuDelete.IsEnabled = !branch.IsCurrent && !branch.IsRemote;
        BranchMenuDeleteRemote.Visibility = branch.IsRemote ? Visibility.Visible : Visibility.Collapsed;
        BranchMenuSetUpstream.IsEnabled = !branch.IsRemote && Vm?.HasRemote == true;
        BranchMenuUnsetUpstream.IsEnabled = !branch.IsRemote && branch.Upstream is not null;
        BranchMenuPull.IsEnabled = !branch.IsRemote && branch.Upstream is not null && Vm?.HasRemote == true;
        BranchMenuPush.IsEnabled = !branch.IsRemote && Vm?.HasRemote == true;
        BranchMenuPushForce.IsEnabled = BranchMenuPush.IsEnabled;
        return true;
    }

    private async void OnBranchPushForce(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch) return;
        if (GitBranchDialogs.ConfirmForcePush(Window.GetWindow(this), branch.Name))
            await vm.PushBranchAsync(branch, force: true);
    }

    private async void OnBranchDeleteRemote(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { IsRemote: true } branch) return;
        if (GitBranchDialogs.ConfirmDeleteRemoteBranch(Window.GetWindow(this), branch.Name))
            await vm.DeleteRemoteBranchAsync(branch);
    }

    private async void OnBranchSetUpstream(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch) return;
        var upstream = GitBranchDialogs.PromptUpstream(Window.GetWindow(this), vm, branch);
        if (!string.IsNullOrWhiteSpace(upstream))
            await vm.SetUpstreamAsync(branch, upstream);
    }

    private async void OnBranchUnsetUpstream(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.UnsetUpstreamAsync(branch);
    }

    /// <summary>ダブルクリックと同じ「右のコミットグラフをこのブランチに切り替える」を右クリックからも。</summary>
    private async void OnBranchShowLog(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.ShowBranchLogAsync(branch);
    }

    private async void OnBranchCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.Commands.CheckoutBranchAsync(branch);
    }

    private async void OnBranchMerge(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.Commands.MergeAsync(branch);
    }

    private async void OnBranchMergeFastForwardOnly(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.Commands.MergeAsync(branch, GitMergeStrategy.FastForwardOnly);
    }

    private async void OnBranchMergeNoFastForward(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.Commands.MergeAsync(branch, GitMergeStrategy.NoFastForward);
    }

    private async void OnBranchMergeSquash(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.Commands.MergeAsync(branch, GitMergeStrategy.Squash);
    }

    private async void OnBranchRebase(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"現在のブランチを {branch.Name} の上へリベースします。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "リベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.Commands.RebaseAsync(branch);
    }

    private async void OnBranchCreateFrom(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            $"{branch.Name} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.Commands.CreateBranchAsync(name, branch.Name);
    }

    private void OnBranchCopyName(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedBranch?.Name);

    private async void OnBranchPull(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.PullBranchAsync(branch);
    }

    private async void OnBranchPush(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.PushBranchAsync(branch);
    }

    private async void OnBranchDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"ブランチ {branch.Name} を削除しますか？",
            "ブランチ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var result = await vm.Commands.DeleteBranchAsync(branch, force: false);
        if (result is { Success: false } &&
            result.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            var forceAnswer = MessageBox.Show(Window.GetWindow(this)!,
                $"{branch.Name} はマージされていないコミットを含みます。強制削除（-D）しますか？\nコミットが失われる可能性があります。",
                "ブランチの強制削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (forceAnswer == MessageBoxResult.Yes)
                await vm.Commands.DeleteBranchAsync(branch, force: true);
        }
    }

    // ===== 操作バー（ブランチ一覧の左の縦帯）=====

    /// <summary>
    /// 選んだ1本に効く操作（チェックアウト／マージ／削除）の可否を選択に合わせる。
    /// 判定は行の右クリックメニュー（<see cref="TryPrepareBranchMenu"/>）と同じ——同じ操作が
    /// 入口ごとに違う可否を見せると、押せないのか効かないのか分からなくなる。
    /// </summary>
    private void OnBranchSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        UpdateBranchOpButtons();

    private void UpdateBranchOpButtons()
    {
        var branch = SelectedTreeBranch;
        BranchOpCheckout.IsEnabled = branch is { IsCurrent: false };
        BranchOpMerge.IsEnabled = branch is { IsCurrent: false };
        BranchOpDelete.IsEnabled = branch is { IsCurrent: false, IsRemote: false };
    }

    /// <summary>
    /// 新しいブランチ。起点は一覧で選んだブランチで、選んでいなければ現在ブランチ（＝素の
    /// <c>git branch</c>）——「押したのに何も起きない」を作らないため、無選択でも必ず作れる。
    /// </summary>
    private async void OnBarBranchCreate(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var start = SelectedTreeBranch;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            start is null
                ? "ブランチ名を入力してください"
                : $"{start.Name} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.Commands.CreateBranchAsync(name, start?.Name);
    }

    private Task PullWithModeAsync(GitPullMode mode) =>
        Vm is { } vm ? vm.PullWithModeAsync(mode) : Task.CompletedTask;

    private async void OnBarPullMerge(object sender, RoutedEventArgs e) =>
        await PullWithModeAsync(GitPullMode.Merge);
    private async void OnBarPullRebase(object sender, RoutedEventArgs e) =>
        await PullWithModeAsync(GitPullMode.Rebase);
    private async void OnBarPullFastForward(object sender, RoutedEventArgs e) =>
        await PullWithModeAsync(GitPullMode.FastForwardOnly);

    private async void OnBarPushNormal(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.Commands.PushAsync();
    }

    /// <summary>強制プッシュ。相手は上流（表示が無ければ現在ブランチ）で、選択行とは関係がない。</summary>
    private async void OnBarPushForce(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var target = vm.UpstreamLabel.Length > 0 ? vm.UpstreamLabel : "現在のブランチ";
        if (GitBranchDialogs.ConfirmForcePush(Window.GetWindow(this), target))
            await vm.PushForceAsync();
    }

    /// <summary>
    /// 一覧の開閉をまとめて切り替える。TreeViewItem ではなくモデル（<see cref="BranchTreeNode"/>）を
    /// 書き換えるのは、畳まれた枝のコンテナはまだ存在しないため——見えている行だけ開いても
    /// 「すべて」にならない。
    /// </summary>
    private void OnBarExpandAll(object sender, RoutedEventArgs e) => SetBranchTreeExpanded(true);
    private void OnBarCollapseAll(object sender, RoutedEventArgs e) => SetBranchTreeExpanded(false);

    private void SetBranchTreeExpanded(bool expanded)
    {
        if (Vm?.PaneFilteredBranchTree is { } tree)
            BranchTreeBuilder.SetExpandedAll(tree, expanded);
    }

    // ===== タグ操作 =====

    private GitTagInfo? SelectedTag => TagList.SelectedItem as GitTagInfo;

    private async void OnTagDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && SelectedTag is { } tag)
            await vm.Commands.CheckoutTagAsync(tag);
    }

    private async void OnTagCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedTag is { } tag)
            await vm.Commands.CheckoutTagAsync(tag);
    }

    private async void OnTagPush(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedTag is { } tag)
            await vm.Commands.PushTagAsync(tag);
    }

    private void OnTagCopyName(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedTag?.Name);

    private async void OnTagDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedTag is not { } tag)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"タグ {tag.Name} を削除しますか？",
            "タグ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.Commands.DeleteTagAsync(tag);
    }

    private async void OnTagCreate(object sender, RoutedEventArgs e) => await CreateTagAsync(target: null);

    private async void OnCommitCreateTag(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { } row)
            await CreateTagAsync(row.Hash);
    }

    private async void OnTagsPushAll(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.Commands.PushAllTagsAsync();
    }

    /// <summary>タグ名（必須）→メッセージ（任意）の順に入力を取り、作成する。</summary>
    private async Task CreateTagAsync(string? target)
    {
        if (Vm is not { } vm)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "タグを作成", "タグ名を入力してください");
        if (string.IsNullOrWhiteSpace(name))
            return;
        var message = InputDialog.Prompt(Window.GetWindow(this), "タグを作成",
            "注釈メッセージ（空なら軽量タグ）:", allowEmpty: true);
        if (message is null)
            return; // メッセージ入力でキャンセル
        await vm.Commands.CreateTagAsync(name, target, string.IsNullOrWhiteSpace(message) ? null : message);
    }

    // ===== サブモジュール操作 =====

    private GitSubmoduleInfo? SelectedSubmodule => SubmoduleList.SelectedItem as GitSubmoduleInfo;

    private async void OnSubmoduleInit(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedSubmodule is { } submodule)
            await vm.Commands.InitSubmoduleAsync(submodule);
    }

    private async void OnSubmoduleUpdate(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedSubmodule is { } submodule)
            await vm.Commands.UpdateSubmoduleAsync(submodule);
    }

    private async void OnSubmodulesSync(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.Commands.SyncSubmodulesAsync();
    }

    private void OnSubmoduleCopyPath(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedSubmodule?.Path);

    // ===== コミット操作 =====

    private GitLogRow? SelectedCommit =>
        LogList.SelectedItem is GitLogRow { IsCommit: true } row ? row : null;

    /// <summary>
    /// コミット一覧を末尾付近までスクロールしたら次ページを追加読み込みする（無限スクロール）。
    /// 下方向のスクロール（またはビューポート縮小）でのみ判定し、追加読み込み後の伸長で連鎖発火しないよう
    /// 純粋な内容伸長（VerticalChange・ViewportHeightChange が 0）は無視する。仮想化は既定の行単位スクロール
    /// なので Extent/Offset/Viewport は行数単位だが、末尾までの残り行数で判定する式は同じく成立する。
    /// </summary>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (e.VerticalChange <= 0 && e.ViewportHeightChange <= 0)
            return;
        if (e.ExtentHeight <= 0)
            return;
        var remaining = e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight);
        if (remaining <= e.ViewportHeight)
            _ = vm.History.LoadMoreAsync();
    }

    /// <summary>選択コミットの差分を Diff セッションへ（1件=コミットの変更、複数=端点間の比較）。</summary>
    private void OnCommitShowDiff(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var rows = LogList.SelectedItems.OfType<GitLogRow>().Where(r => r.IsCommit).ToList();
        vm.OpenDiffForCommits(rows);
    }

    private async void OnCommitCreateBranch(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            $"コミット {row.ShortHash} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.Commands.CreateBranchAsync(name, row.Hash);
    }

    private async void OnCommitCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.CheckoutCommitAsync(row);
    }

    private async void OnCommitRewriteMessage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var current = await vm.Commands.GetCommitMessageAsync(row);
        var message = InputDialog.Prompt(Window.GetWindow(this), "コミットメッセージを修正",
            $"{row.ShortHash} のコミットメッセージを入力してください。\nこのコミット以降の履歴が書き換わります。",
            current, multiline: true);
        if (message is null || string.Equals(message, current, StringComparison.Ordinal))
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} 以降のコミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "コミットメッセージを修正", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.Commands.RewriteCommitMessageAsync(row, message);
    }

    /// <summary>選択中のコミット件数（グラフ継続行は除く）。</summary>
    private int SelectedCommitCount =>
        LogList.SelectedItems.OfType<GitLogRow>().Count(r => r.IsCommit);

    /// <summary>
    /// コミット一覧のコンテキストメニューを開く直前に、意味を持たない項目を落とす——スカッシュは2件以上、
    /// インタラクティブリベースとファイル履歴の「この版の…」は単一選択時だけ、GitHub の項目は
    /// GitHub リポジトリのときだけ、作者の絞り込みは既にその作者だけを見ていないときだけ。
    /// </summary>
    private void OnCommitContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        SquashMenuItem.Visibility = SelectedCommitCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
        InteractiveRebaseMenuItem.Visibility = SelectedCommitCount == 1 ? Visibility.Visible : Visibility.Collapsed;

        // 「この版の…」はファイル1件の履歴を見ているときだけ意味を持つ（どのファイルの版か決まらないため）
        var fileRevision = Vm?.IsFileHistory == true && SelectedCommitCount == 1
            ? Visibility.Visible : Visibility.Collapsed;
        FileRevisionSeparator.Visibility = fileRevision;
        FileRevisionMenuItem.Visibility = fileRevision;

        // GitHub 以外のホスティング（あるいはリモート無し）では「GitHub で開く」を出さない
        // ——押せるのに何も起きない項目を並べない。
        var hosting = Vm?.IsGitHubRepository == true && SelectedCommit is not null
            ? Visibility.Visible : Visibility.Collapsed;
        OpenOnHostingMenuItem.Visibility = hosting;
        CopyHostingUrlMenuItem.Visibility = hosting;

        // 作者の絞り込みは、既にその作者だけを見ているなら押しても何も変わらないので無効にする。
        FilterByAuthorMenuItem.Visibility = SelectedCommit?.Author is { Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        FilterByAuthorMenuItem.IsEnabled = SelectedCommit is { } authorRow && Vm is { } authorVm
            && !authorVm.IsFilteredByAuthor(authorRow);
    }

    // ===== 特定リビジョンのファイル（ファイル履歴中のみ） =====

    private async void OnCommitOpenFileRevision(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.OpenFileAtRevisionAsync(row);
    }

    private async void OnCommitCompareFileRevision(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.CompareFileWithRevisionAsync(row);
    }

    private async void OnCommitRestoreFileRevision(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row) return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{vm.History.ScopedPath} を {row.ShortHash} の時点の内容へ戻します。\n" +
            "作業ツリーの現在の内容は失われます（履歴は書き換えません）。\n\n実行しますか？",
            "この版の内容へ戻す", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.RestoreFileAtRevisionAsync(row);
    }

    // ===== リモート =====

    private async void OnRemoteAdd(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var owner = Window.GetWindow(this);
        var name = InputDialog.Prompt(owner, "リモートを追加",
            "リモート名を入力してください（例: origin）", vm.Remotes.Count == 0 ? "origin" : "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = InputDialog.Prompt(owner, "リモートを追加", $"{name} の URL を入力してください");
        if (!string.IsNullOrWhiteSpace(url))
            await vm.AddRemoteAsync(name, url);
    }

    private async void OnRemoteSetUrl(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || RemoteList.SelectedItem is not GitRemoteInfo remote) return;
        var url = InputDialog.Prompt(Window.GetWindow(this), "リモートの URL を変更",
            $"{remote.Name} の URL", remote.Url);
        if (!string.IsNullOrWhiteSpace(url) && url != remote.Url)
            await vm.SetRemoteUrlAsync(remote.Name, url);
    }

    private void OnRemoteCopyUrl(object sender, RoutedEventArgs e) =>
        CopyToClipboard((RemoteList.SelectedItem as GitRemoteInfo)?.Url);

    private async void OnRemoteRemove(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || RemoteList.SelectedItem is not GitRemoteInfo remote) return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"リモート {remote.Name}（{remote.Url}）を削除しますか？\n" +
            "追跡ブランチと上流の設定も一緒に消えます（リモート側のリポジトリはそのままです）。",
            "リモートの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.RemoveRemoteAsync(remote.Name);
    }

    /// <summary>
    /// 選択コミットからHEADまでをインタラクティブリベースする。候補取得→確認→ダイアログ→実行の順。
    /// </summary>
    private async void OnCommitInteractiveRebase(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var (entries, error) = await vm.Commands.GetRebaseCandidatesAsync(row);
        if (error is not null)
        {
            ToastService.Error(error);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} から HEAD までの履歴が書き換わります。実行しますか？",
            "インタラクティブリベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        var plan = InteractiveRebaseDialog.Show(Window.GetWindow(this), entries);
        if (plan is null)
            return;
        await vm.Commands.InteractiveRebaseAsync(row.Hash!, plan.Value.Plan, plan.Value.Messages);
    }

    /// <summary>選択した複数コミットを1つにまとめる（squash）。履歴を書き換えるので確認を取る。</summary>
    private async void OnCommitSquash(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var rows = LogList.SelectedItems.OfType<GitLogRow>().Where(r => r.IsCommit).ToList();
        if (rows.Count < 2)
            return;  // メニューは2件以上のときだけ出るが念のため
        var combinedMessage = await vm.GetCombinedCommitMessageAsync(rows);
        var message = InputDialog.Prompt(Window.GetWindow(this), "スカッシュ後のコミットメッセージ",
            "スカッシュ後に使用するコミットメッセージを編集してください。",
            combinedMessage, multiline: true);
        if (message is null)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"選択した {rows.Count} 件のコミットを1つにまとめます。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "スカッシュ", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.Commands.SquashAsync(rows, message);
    }

    private async void OnCommitCherryPick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.CherryPickAsync(row);
    }

    private async void OnCommitCherryPickNoCommit(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.CherryPickNoCommitAsync(row);
    }

    private async void OnCommitRevert(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.RevertAsync(row);
    }

    private async void OnCommitRevertNoCommit(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.RevertNoCommitAsync(row);
    }

    private async void OnCommitResetSoft(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.ResetAsync(row, GitResetMode.Soft);
    }

    private async void OnCommitResetMixed(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.Commands.ResetAsync(row, GitResetMode.Mixed);
    }

    private async void OnCommitResetHard(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} まで hard リセットします。作業ツリー・インデックスの変更はすべて失われます。\n実行しますか？",
            "リセット (hard)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.Commands.ResetAsync(row, GitResetMode.Hard);
    }

    // ===== 調べる・たどる =====

    /// <summary>一覧の絞り込みをこのコミットの作者だけに切り替える（絞り込み帯の作者欄と同じ状態になる）。</summary>
    private void OnCommitFilterByAuthor(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            vm.FilterByAuthor(row);
    }

    private void OnCommitOpenOnHosting(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            vm.OpenCommitOnHosting(row);
    }

    // ===== パッチ・コピー =====

    private async void OnCommitOpenPatch(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.OpenPatchAsync(row);
    }

    private async void OnCommitCopyPatch(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        CopyToClipboard(await vm.GetCommitPatchAsync(row));
    }

    private void OnCommitCopyHash(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedCommit?.Hash);

    private void OnCommitCopyShortHash(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedCommit?.ShortHash);

    private void OnCommitCopySubject(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedCommit?.Subject);

    private void OnCommitCopyAuthor(object sender, RoutedEventArgs e) =>
        CopyToClipboard(SelectedCommit?.Author);

    /// <summary>「0c92f1e 件名」——issue やレビューに貼るときの定型。</summary>
    private void OnCommitCopySummary(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is not { } row) return;
        CopyToClipboard($"{row.ShortHash} {row.Subject}".Trim());
    }

    private void OnCommitCopyHostingUrl(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            CopyToClipboard(vm.CommitHostingUrl(row));
    }

    /// <summary>クリップボードへ渡す唯一の場所（このビューの「〜をコピー」は全部ここを通る）。
    /// 空は何もせず、他プロセスが占有している間の失敗は黙って捨てる。</summary>
    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* クリップボード占有中は無視 */ }
    }
}
