using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>Gitセッションの列表示状態を購読し、列幅の保存と復元を行う。</summary>
internal sealed class GitSessionColumnVisibilityController
{
    private const double CollapsedBranchColumnWidth = 29;

    private readonly ColumnDefinition _branchSplitterColumn;
    private readonly ColumnDefinition _branchColumn;
    private readonly UIElement _branchSplitter;
    private readonly UIElement _branchListArea;
    private readonly UIElement _branchOperationGroup;
    private readonly FrameworkElement _branchOperationBar;
    private readonly ColumnDefinition _commitDetailSplitterColumn;
    private readonly ColumnDefinition _commitDetailColumn;
    private readonly UIElement _commitDetailSplitter;
    private readonly UIElement _commitDetailPanel;
    private GitSessionViewModel? _viewModel;
    private GridLength _commitDetailWidth = new(300);
    private GridLength _branchColumnWidth = new(190);
    private bool? _branchColumnApplied;

    internal GitSessionColumnVisibilityController(
        ColumnDefinition branchSplitterColumn,
        ColumnDefinition branchColumn,
        UIElement branchSplitter,
        UIElement branchListArea,
        UIElement branchOperationGroup,
        FrameworkElement branchOperationBar,
        ColumnDefinition commitDetailSplitterColumn,
        ColumnDefinition commitDetailColumn,
        UIElement commitDetailSplitter,
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
    }

    internal void Attach(GitSessionViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

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
            _branchColumn.ActualWidth, _branchOperationBar.ActualWidth, CollapsedBranchColumnWidth,
            _branchSplitterColumn, _branchColumn, _branchSplitter, _branchListArea, _branchOperationGroup);
        _branchColumnWidth = state.Width;
        _branchColumnApplied = state.IsApplied;
    }

    private void ApplyCommitDetailVisibility()
    {
        var visible = _viewModel?.CommitDetailVisible ?? true;
        _commitDetailWidth = GitSessionColumnVisibilityPresenter.ApplyCommitDetailColumn(
            visible, _commitDetailWidth, _commitDetailColumn.ActualWidth,
            _commitDetailSplitterColumn, _commitDetailColumn, _commitDetailSplitter, _commitDetailPanel);
    }
}
