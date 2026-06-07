using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class AiBarView : UserControl
{
    private readonly HashSet<TranscriptEntry> _observedEntries = new();
    private AiBarViewModel? _viewModel;
    private bool _scrollQueued;
    // 末尾追従中か。ユーザーが上へスクロールすると false（追従停止）、再び最下部まで戻すと true（追従再開）。
    private bool _autoScrollEnabled = true;

    public AiBarView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AttachViewModel(DataContext as AiBarViewModel);
            QueueScrollToEnd();
        };
        Unloaded += (_, _) => AttachViewModel(null);
        DataContextChanged += (_, e) => AttachViewModel(e.NewValue as AiBarViewModel);
    }

    /// <summary>AI 入力欄へキーボードフォーカスを移す（ペイン間ナビゲーション用）。</summary>
    public void FocusInput() => InputBox.Focus();

    private void AttachViewModel(AiBarViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Transcript.CollectionChanged -= OnTranscriptCollectionChanged;
        }

        foreach (var entry in _observedEntries)
            entry.PropertyChanged -= OnTranscriptEntryPropertyChanged;
        _observedEntries.Clear();

        _viewModel = viewModel;
        if (_viewModel is null) return;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Transcript.CollectionChanged += OnTranscriptCollectionChanged;
        foreach (var entry in _viewModel.Transcript)
            ObserveEntry(entry);
        QueueScrollToEnd();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiBarViewModel.IsExpanded))
            QueueScrollToEnd();
    }

    private void OnTranscriptCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TranscriptEntry entry in e.OldItems)
                UnobserveEntry(entry);
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _observedEntries)
                entry.PropertyChanged -= OnTranscriptEntryPropertyChanged;
            _observedEntries.Clear();
            if (_viewModel is not null)
            {
                foreach (var entry in _viewModel.Transcript)
                    ObserveEntry(entry);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TranscriptEntry entry in e.NewItems)
                ObserveEntry(entry);
        }

        QueueScrollToEnd();
    }

    private void ObserveEntry(TranscriptEntry entry)
    {
        if (!_observedEntries.Add(entry)) return;
        entry.PropertyChanged += OnTranscriptEntryPropertyChanged;
    }

    private void UnobserveEntry(TranscriptEntry entry)
    {
        if (!_observedEntries.Remove(entry)) return;
        entry.PropertyChanged -= OnTranscriptEntryPropertyChanged;
    }

    private void OnTranscriptEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranscriptEntry.Text)
            or nameof(TranscriptEntry.Header)
            or nameof(TranscriptEntry.HasDiff)
            or nameof(TranscriptEntry.IsPending)
            or nameof(TranscriptEntry.IsCollapsed))
        {
            QueueScrollToEnd();
        }
    }

    private void QueueScrollToEnd()
    {
        if (_scrollQueued || !IsLoaded) return;

        _scrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollQueued = false;
            // ユーザーが上へスクロールして追従を止めている間は末尾へ飛ばさない。
            if (_autoScrollEnabled
                && _viewModel?.IsExpanded == true
                && TranscriptScrollViewer.Visibility == Visibility.Visible)
                TranscriptScrollViewer.ScrollToEnd();
        }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// ユーザー操作によるスクロールを検知して末尾追従の有効/無効を切り替える。
    /// コンテンツ増加（ExtentHeightChange != 0）による変化は無視し、ユーザーが上へ動かしたら追従を止め、
    /// 最下部まで戻したら追従を再開する。
    /// </summary>
    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 出力でコンテンツが伸びたことによる変化は判定対象外（追従中の自動スクロールを誤判定しない）。
        if (e.ExtentHeightChange != 0) return;

        const double tolerance = 1.0;
        bool atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - tolerance;
        _autoScrollEnabled = atBottom;
    }

    /// <summary>補完ポップアップ操作（開いているとき）と入力履歴呼び出し（閉じているときの↑/↓）を処理する。</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not AiBarViewModel vm) return;

        if (vm.IsCommandPopupOpen)
        {
            switch (e.Key)
            {
                case Key.Down: vm.MoveCommandSelection(1); e.Handled = true; break;
                case Key.Up: vm.MoveCommandSelection(-1); e.Handled = true; break;
                // 候補が確定できないときは Tab/Enter を素通しし、本来の挙動（フォーカス移動・通常送信）に委ねる。
                case Key.Tab: e.Handled = vm.CompleteSelectedCommand(); break;
                case Key.Enter: e.Handled = vm.AcceptAndRunSelectedCommand(); break;
                case Key.Escape: vm.CloseCommandPopup(); e.Handled = true; break;
            }
            return;
        }

        // ポップアップが閉じているときの↑/↓は、送信済みプロンプトの履歴をたどる。
        switch (e.Key)
        {
            case Key.Up:
                if (vm.RecallPreviousHistory()) { MoveCaretToEnd(); e.Handled = true; }
                break;
            case Key.Down:
                if (vm.RecallNextHistory()) { MoveCaretToEnd(); e.Handled = true; }
                break;
        }
    }

    /// <summary>履歴呼び出し後、キャレットを末尾へ移す（編集を続けやすく）。</summary>
    private void MoveCaretToEnd()
        => Dispatcher.BeginInvoke(() => InputBox.CaretIndex = InputBox.Text.Length,
            DispatcherPriority.Input);

    /// <summary>補完候補をクリックで確定・実行する。</summary>
    private void OnCommandListClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AiBarViewModel vm)
            vm.AcceptAndRunSelectedCommand();
    }

    /// <summary>
    /// 選択可能にした読み取り専用 TextBox 上のホイール操作を外側の ScrollViewer へ転送する
    /// （TextBox がイベントを握り潰してトランスクリプトがスクロールしなくなるのを防ぐ）。
    /// </summary>
    private void OnTranscriptPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not Control source) return;
        e.Handled = true;
        TranscriptScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = source,
        });
    }
}
