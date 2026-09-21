using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>AI transcriptの変更購読、末尾追従、本文上のホイール転送を管理する。</summary>
internal sealed class AiTranscriptScrollController
{
    private readonly ScrollViewer _scrollViewer;
    private readonly HashSet<TranscriptEntry> _observedEntries = new();
    private AiBarViewModel? _viewModel;
    private bool _scrollQueued;
    // 上へ読みに戻ったユーザーを自動で末尾へ戻さない。
    private bool _autoScrollEnabled = true;

    internal AiTranscriptScrollController(ScrollViewer scrollViewer)
        => _scrollViewer = scrollViewer;

    internal void OnLoaded(AiBarViewModel? viewModel)
    {
        AttachViewModel(viewModel);
        QueueScrollToEnd();
    }

    internal void OnUnloaded() => AttachViewModel(null);

    internal void AttachViewModel(AiBarViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Transcript.CollectionChanged -= OnTranscriptCollectionChanged;
        }

        foreach (var entry in _observedEntries)
            entry.PropertyChanged -= OnTranscriptEntryPropertyChanged;
        _observedEntries.Clear();

        _viewModel = viewModel;
        if (_viewModel is null)
            return;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Transcript.CollectionChanged += OnTranscriptCollectionChanged;
        foreach (var entry in _viewModel.Transcript)
            ObserveEntry(entry);
        QueueScrollToEnd();
    }

    internal void OnScrollChanged(ScrollChangedEventArgs e)
    {
        // コンテンツの伸びはユーザー操作ではないため追従状態を変えない。
        if (e.ExtentHeightChange != 0)
            return;

        _autoScrollEnabled = DebugOutputCollectionUpdater.IsAtBottom(
            e.ExtentHeight, e.ViewportHeight, e.VerticalOffset, tolerance: 1.0);
    }

    internal void ForwardMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not Control source)
            return;

        e.Handled = true;
        _scrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source,
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiBarViewModel.IsExpanded))
            QueueScrollToEnd();
    }

    private void OnTranscriptCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TranscriptEntry entry in e.OldItems)
                UnobserveEntry(entry);

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _observedEntries)
                entry.PropertyChanged -= OnTranscriptEntryPropertyChanged;
            _observedEntries.Clear();
            if (_viewModel is not null)
                foreach (var entry in _viewModel.Transcript)
                    ObserveEntry(entry);
        }

        if (e.NewItems is not null)
            foreach (TranscriptEntry entry in e.NewItems)
                ObserveEntry(entry);

        QueueScrollToEnd();
    }

    private void ObserveEntry(TranscriptEntry entry)
    {
        if (_observedEntries.Add(entry))
            entry.PropertyChanged += OnTranscriptEntryPropertyChanged;
    }

    private void UnobserveEntry(TranscriptEntry entry)
    {
        if (_observedEntries.Remove(entry))
            entry.PropertyChanged -= OnTranscriptEntryPropertyChanged;
    }

    private void OnTranscriptEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranscriptEntry.Text)
            or nameof(TranscriptEntry.Header)
            or nameof(TranscriptEntry.HasDiff)
            or nameof(TranscriptEntry.IsPending)
            or nameof(TranscriptEntry.IsCollapsed))
            QueueScrollToEnd();
    }

    private void QueueScrollToEnd()
    {
        if (_scrollQueued || !_scrollViewer.IsLoaded)
            return;

        _scrollQueued = true;
        _scrollViewer.Dispatcher.BeginInvoke(() =>
        {
            _scrollQueued = false;
            if (_autoScrollEnabled
                && _viewModel?.IsExpanded == true
                && _scrollViewer.Visibility == Visibility.Visible)
                _scrollViewer.ScrollToEnd();
        }, DispatcherPriority.ContextIdle);
    }
}
