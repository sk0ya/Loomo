using System.Collections.Specialized;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>DiffセッションVMのイベントをビューの表示処理へ結び、差し替え時に購読を整理する。</summary>
internal sealed class DiffSessionBindingController : IDisposable
{
    private readonly Action<int> _scrollToRow;
    private readonly Action _autoJump;
    private readonly Action<int> _scrollToConflict;
    private readonly NotifyCollectionChangedEventHandler _diffRowsChanged;
    private readonly NotifyCollectionChangedEventHandler _sideRowsChanged;
    private readonly Action<DiffSessionViewModel?> _viewModelChanged;
    private readonly Action _scheduleUnified;
    private readonly Action _scheduleSide;
    private DiffSessionViewModel? _viewModel;
    private bool _disposed;

    internal DiffSessionBindingController(
        Action<int> scrollToRow,
        Action autoJump,
        Action<int> scrollToConflict,
        NotifyCollectionChangedEventHandler diffRowsChanged,
        NotifyCollectionChangedEventHandler sideRowsChanged,
        Action<DiffSessionViewModel?> viewModelChanged,
        Action scheduleUnified,
        Action scheduleSide)
    {
        _scrollToRow = scrollToRow;
        _autoJump = autoJump;
        _scrollToConflict = scrollToConflict;
        _diffRowsChanged = diffRowsChanged;
        _sideRowsChanged = sideRowsChanged;
        _viewModelChanged = viewModelChanged;
        _scheduleUnified = scheduleUnified;
        _scheduleSide = scheduleSide;
    }

    internal void SetViewModel(DiffSessionViewModel? viewModel)
    {
        if (_disposed || ReferenceEquals(_viewModel, viewModel))
            return;
        Unhook();
        _viewModel = viewModel;
        _viewModelChanged(viewModel);
        if (viewModel is null)
            return;

        viewModel.ScrollToRowRequested += _scrollToRow;
        viewModel.AutoJumpRequested += _autoJump;
        viewModel.Conflict.ScrollToConflictRequested += _scrollToConflict;
        viewModel.DiffRows.CollectionChanged += _diffRowsChanged;
        viewModel.SideRows.CollectionChanged += _sideRowsChanged;
        _scheduleUnified();
        _scheduleSide();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unhook();
        _viewModelChanged(null);
    }

    private void Unhook()
    {
        if (_viewModel is null)
            return;
        _viewModel.ScrollToRowRequested -= _scrollToRow;
        _viewModel.AutoJumpRequested -= _autoJump;
        _viewModel.Conflict.ScrollToConflictRequested -= _scrollToConflict;
        _viewModel.DiffRows.CollectionChanged -= _diffRowsChanged;
        _viewModel.SideRows.CollectionChanged -= _sideRowsChanged;
        _viewModel = null;
    }
}
