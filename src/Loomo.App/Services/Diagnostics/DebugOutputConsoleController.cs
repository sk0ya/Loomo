using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Documents;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ出力コレクションと RichTextBox の文書・スクロールを同期する。</summary>
internal sealed class DebugOutputConsoleController : IDisposable
{
    private readonly RichTextBox _console;
    private readonly Action _showInspection;
    private readonly Action _showOutput;
    private DebugManagerViewModelBase? _viewModel;
    private INotifyCollectionChanged? _observed;

    internal DebugOutputConsoleController(RichTextBox console, Action showInspection, Action showOutput)
    {
        _console = console;
        _showInspection = showInspection;
        _showOutput = showOutput;
    }

    internal void Attach(DebugManagerViewModelBase? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.OutputRequested -= OnOutputRequested;
        }
        ObserveOutput(null);

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.OutputRequested += OnOutputRequested;
        }
        Rebuild();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;
        if (e.PropertyName == nameof(DebugManagerViewModelBase.Output))
        {
            ObserveOutput(vm.Output);
            Rebuild();
        }
        DebugSessionTabPolicy.ApplyForStateChange(
            e.PropertyName == nameof(DebugManagerViewModelBase.IsStopped), vm.IsStopped,
            e.PropertyName == nameof(DebugManagerViewModelBase.IsBusy), vm.IsBusy,
            _showInspection, _showOutput);
    }

    private void OnOutputRequested() => _showOutput();

    public void ClearOutput() => _viewModel?.ClearOutput();

    private void ObserveOutput(INotifyCollectionChanged? output)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnOutputChanged;
        _observed = output;
        if (_observed is not null)
            _observed.CollectionChanged += OnOutputChanged;
    }

    private void Rebuild()
        => DebugOutputCollectionUpdater.Rebuild(
            _viewModel?.Output, _console.Document.Blocks.Clear,
            AppendLine, _console.ScrollToEnd);

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DebugOutputCollectionUpdater.ApplyChange(e,
            () => DebugOutputCollectionUpdater.IsAtBottom(
                _console.ExtentHeight, _console.ViewportHeight, _console.VerticalOffset),
            AppendLine,
            RemoveFirstLine,
            _console.Document.Blocks.Clear,
            _console.ScrollToEnd);

    private void AppendLine(DebugOutputLine line)
        => DebugOutputDocumentRenderer.Append(_console.Document, line);

    private void RemoveFirstLine()
    {
        if (_console.Document.Blocks.FirstBlock is { } block)
            _console.Document.Blocks.Remove(block);
    }

    public void Dispose() => Attach(null);
}
