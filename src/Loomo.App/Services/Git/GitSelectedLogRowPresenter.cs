using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Git履歴の選択行をListViewの選択・表示位置へ同期する。</summary>
internal sealed class GitSelectedLogRowPresenter
{
    private readonly ListView _logList;
    private GitHistoryViewModel? _history;
    private bool _isRevealing;

    internal GitSelectedLogRowPresenter(ListView logList) => _logList = logList;

    internal void Attach(GitHistoryViewModel? history)
    {
        if (_history is not null)
            _history.PropertyChanged -= OnHistoryPropertyChanged;
        _history = history;
        if (_history is not null)
            _history.PropertyChanged += OnHistoryPropertyChanged;
    }

    private void OnHistoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitHistoryViewModel.SelectedLogRow)
            && _history?.SelectedLogRow is { } row && !_isRevealing)
            _logList.Dispatcher.BeginInvoke(() => SelectAndReveal(row));
    }

    private void SelectAndReveal(GitLogRow row)
    {
        if (!_logList.Items.Contains(row))
            return;
        _isRevealing = true;
        try
        {
            // Clear→再選択はVM通知を再発火させ、Dispatcherに同じ処理を積み続ける。
            if (!ReferenceEquals(_logList.SelectedItem, row))
                _logList.SelectedItem = row;
            _logList.ScrollIntoView(row);
            _logList.UpdateLayout();
            if (_logList.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem item)
                item.BringIntoView();
        }
        finally
        {
            _isRevealing = false;
        }
    }
}
