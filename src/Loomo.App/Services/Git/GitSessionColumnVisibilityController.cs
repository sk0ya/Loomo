using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>Gitセッションの列表示状態を購読し、列幅の保存と復元を行う。
///
/// <para><b>左右の列はペインが狭まっても画面の外へ押し出さない。</b>両脇は px 指定、中央（グラフ）は
/// 残りを取る * 列なので、何もしないと中央が最小幅に達したところで Grid がはみ出し、右端の詳細列から
/// 切れて見えなくなる。そこで Grid の幅が変わるたびに両脇の <c>MaxWidth</c> を「中央の最小幅を残せる幅」
/// に絞る。<c>Width</c>（人が決めた幅）は触らないので、ペインを広げ直せば元の幅に戻る。</para>
///
/// <para>人が決めた幅（境界のドラッグを終えたとき）は設定へ残し、次に開いたときに戻す。</para>
/// </summary>
internal sealed class GitSessionColumnVisibilityController
{
    private const double CollapsedBranchColumnWidth = 29;
    private const double BranchColumnMinWidth = 120;
    private const double SplitterWidth = 6;

    private readonly ColumnDefinition _branchSplitterColumn;
    private readonly ColumnDefinition _branchColumn;
    private readonly GridSplitter _branchSplitter;
    private readonly UIElement _branchListArea;
    private readonly UIElement _branchOperationGroup;
    private readonly FrameworkElement _branchOperationBar;
    private readonly ColumnDefinition _commitDetailSplitterColumn;
    private readonly ColumnDefinition _commitDetailColumn;
    private readonly GridSplitter _commitDetailSplitter;
    private readonly UIElement _commitDetailPanel;
    private GitSessionViewModel? _viewModel;
    private GridLength _commitDetailWidth = new(1, GridUnitType.Star);
    private GridLength _branchColumnWidth = new(190);
    private bool? _branchColumnApplied;

    internal GitSessionColumnVisibilityController(
        ColumnDefinition branchSplitterColumn,
        ColumnDefinition branchColumn,
        GridSplitter branchSplitter,
        UIElement branchListArea,
        UIElement branchOperationGroup,
        FrameworkElement branchOperationBar,
        ColumnDefinition commitDetailSplitterColumn,
        ColumnDefinition commitDetailColumn,
        GridSplitter commitDetailSplitter,
        UIElement commitDetailPanel)
    {
        _branchSplitterColumn = branchSplitterColumn;
        _branchColumn = branchColumn;
        _branchSplitter = branchSplitter;
        _branchListArea = branchListArea;
        _branchOperationGroup = branchOperationGroup;
        _branchOperationBar = branchOperationBar;
        _commitDetailSplitterColumn = commitDetailSplitterColumn;
        _commitDetailColumn = commitDetailColumn;
        _commitDetailSplitter = commitDetailSplitter;
        _commitDetailPanel = commitDetailPanel;

        _branchSplitter.DragCompleted += OnBranchSplitterDragCompleted;
        _commitDetailSplitter.DragCompleted += OnCommitDetailSplitterDragCompleted;
        // 左列の境界を動かしても外側の Grid の幅は変わらず、内側ははみ出している間 SizeChanged が来ない
        // （下の ClampSideColumns の注記）ので、ドラッグのたびに測り直す。
        _branchSplitter.DragDelta += (_, _) => ClampSideColumns();
        // 自分の列を持つ Grid の幅の変化で、両脇の上限を測り直す（ペイン・ウィンドウ・左列の幅のどれが変わっても来る）。
        if (_branchSplitter.Parent is Grid outer) outer.SizeChanged += (_, _) => ClampSideColumns();
        if (_commitDetailSplitter.Parent is Grid inner) inner.SizeChanged += (_, _) => ClampSideColumns();
    }

    internal void Attach(GitSessionViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_viewModel.Layout.BranchColumnWidth is { } branch)
                _branchColumnWidth = new GridLength(Math.Max(BranchColumnMinWidth, branch));
            if (_viewModel.Layout.CommitDetailRatio is { } ratio)
                ApplyCommitDetailRatio(ratio);
        }

        ApplyCommitDetailVisibility();
        ApplyBranchColumnVisibility();
    }

    internal void ToggleBranchColumn()
    {
        if (_viewModel is { } vm)
            vm.BranchColumnVisible = !vm.BranchColumnVisible;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitSessionViewModel.CommitDetailVisible))
            ApplyCommitDetailVisibility();
        else if (e.PropertyName == nameof(GitSessionViewModel.BranchColumnVisible))
            ApplyBranchColumnVisibility();
    }

    private void ApplyBranchColumnVisibility()
    {
        if (_viewModel is not { } vm)
            return;

        var state = GitSessionColumnVisibilityPresenter.ApplyBranchColumn(
            vm.BranchColumnVisible, _branchColumnApplied, _branchColumnWidth,
            RememberableWidth(_branchColumn), _branchOperationBar.ActualWidth, CollapsedBranchColumnWidth,
            _branchSplitterColumn, _branchColumn, _branchSplitter, _branchListArea, _branchOperationGroup);
        _branchColumnWidth = state.Width;
        _branchColumnApplied = state.IsApplied;
        QueueClampSideColumns();
    }

    private void ApplyCommitDetailVisibility()
    {
        var visible = _viewModel?.CommitDetailVisible ?? true;
        _commitDetailWidth = GitSessionColumnVisibilityPresenter.ApplyCommitDetailColumn(
            visible, _commitDetailWidth, _commitDetailColumn.Width,
            _commitDetailSplitterColumn, _commitDetailColumn, _commitDetailSplitter, _commitDetailPanel);
        QueueClampSideColumns();
    }

    private void OnBranchSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled || !_branchColumn.Width.IsAbsolute) return;
        _branchColumnWidth = _branchColumn.Width;
        if (_viewModel is not { } vm) return;
        vm.Layout.BranchColumnWidth = Math.Round(_branchColumn.Width.Value);
        vm.SaveLayout();
    }

    private void OnCommitDetailSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        // GridSplitter は * 列どうしの境界を動かすと、両方の * の値を今の px 幅に置き換える＝比率はそのまま取れる。
        if (e.Canceled || GraphColumn() is not { } graph) return;
        var detailWidth = _commitDetailColumn.ActualWidth;
        var total = graph.ActualWidth + detailWidth;
        if (total <= 0) return;
        var ratio = Math.Round(detailWidth / total, 4);
        ApplyCommitDetailRatio(ratio);
        if (_viewModel is not { } vm) return;
        vm.Layout.CommitDetailRatio = ratio;
        vm.SaveLayout();
    }

    /// <summary>一覧と詳細の * の値を割合から当て直す（詳細を畳んでいる間は戻すときの値として覚えるだけ）。</summary>
    private void ApplyCommitDetailRatio(double ratio)
    {
        _commitDetailWidth = new GridLength(ratio, GridUnitType.Star);
        if (GraphColumn() is { } graph) graph.Width = new GridLength(1 - ratio, GridUnitType.Star);
        if (_commitDetailPanel.Visibility == Visibility.Visible) _commitDetailColumn.Width = _commitDetailWidth;
    }

    private ColumnDefinition? GraphColumn()
        => _commitDetailSplitter.Parent is Grid inner && inner.ColumnDefinitions.Count > 0
            ? inner.ColumnDefinitions[0] : null;

    /// <summary>畳むときに覚えておく幅。上限で絞られている間の見かけの幅ではなく、人が決めた幅（Width）を取る。</summary>
    private static double RememberableWidth(ColumnDefinition column)
        => column.Width.IsAbsolute && column.ActualWidth > 0 ? column.Width.Value : column.ActualWidth;

    /// <summary>列の出し入れの後は、新しい幅で配置し終えてから測り直す。</summary>
    private void QueueClampSideColumns()
    {
        ClampSideColumns();
        _branchSplitter.Dispatcher.BeginInvoke(new Action(ClampSideColumns), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>両脇の列の上限を、中央が最小幅を保てる幅に絞る（理由はクラスの説明）。</summary>
    private void ClampSideColumns()
    {
        if (_commitDetailSplitter.Parent is Grid inner && inner.ColumnDefinitions.Count > 0)
        {
            // 内側の Grid の ActualWidth は使えない。はみ出している間は「切られる前の幅」（＝詳細列の今の幅込み）を
            // 返すので、上限がいつまでも今の幅のまま絞れない。外側の中央列に割り当てられた幅が本当の持ち分。
            var available = _branchSplitter.Parent is Grid outerGrid && outerGrid.ColumnDefinitions.Count > 2
                ? outerGrid.ColumnDefinitions[2].ActualWidth
                : inner.ActualWidth;
            var graphMin = inner.ColumnDefinitions[0].MinWidth;
            _commitDetailColumn.MaxWidth = GitSessionColumnVisibilityPresenter.SideColumnMaxWidth(
                available, _commitDetailSplitterColumn.Width.Value + graphMin, _commitDetailColumn.MinWidth);
        }
        if (_branchSplitter.Parent is Grid outer && outer.ColumnDefinitions.Count > 2)
        {
            // 中央に要るのは、その列の最小幅と「グラフ＋詳細」の最小幅の大きい方（詳細列を出していればその分も）。
            var detailMin = _commitDetailPanel.Visibility == Visibility.Visible
                ? SplitterWidth + _commitDetailColumn.MinWidth : 0;
            var graphMin = _commitDetailSplitter.Parent is Grid g && g.ColumnDefinitions.Count > 0
                ? g.ColumnDefinitions[0].MinWidth : 0;
            var centerMin = Math.Max(outer.ColumnDefinitions[2].MinWidth, graphMin + detailMin);
            _branchColumn.MaxWidth = GitSessionColumnVisibilityPresenter.SideColumnMaxWidth(
                outer.ActualWidth, _branchSplitterColumn.Width.Value + centerMin, _branchColumn.MinWidth);
        }
    }
}
