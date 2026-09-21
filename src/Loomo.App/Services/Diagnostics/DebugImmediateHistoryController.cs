using System.Collections.Specialized;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>イミディエイト履歴の変更購読、末尾スクロール、Enter評価を同期する。</summary>
internal sealed class DebugImmediateHistoryController(ListBox history)
{
    private INotifyCollectionChanged? _observed;

    public void Attach(DebugInspectionViewModel? viewModel)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnLogChanged;
        _observed = viewModel?.ImmediateLog;
        if (_observed is not null)
            _observed.CollectionChanged += OnLogChanged;
    }

    public void HandleKeyDown(KeyEventArgs e, DebugInspectionViewModel? viewModel)
    {
        if (e.Key == Key.Enter && viewModel is not null
            && viewModel.SubmitImmediateCommand.CanExecute(null))
        {
            viewModel.SubmitImmediateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && history.Items.Count > 0)
            history.ScrollIntoView(history.Items[history.Items.Count - 1]);
    }
}
