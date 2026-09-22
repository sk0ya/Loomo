using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧の列幅計測・自動配分と、見出しグリップのドラッグを同期する。</summary>
internal sealed class FilesColumnWidthPresenter
{
    private readonly ListBox _entryList;
    private readonly Control _resourceScope;
    private FilesColumnViewModel? _viewModel;
    private bool _autoWidthsQueued;
    private int _autoWidthGeneration;
    private Dictionary<FilesColumnKey, double>? _contentWidths;

    internal FilesColumnWidthPresenter(ListBox entryList, Control resourceScope)
    {
        _entryList = entryList;
        _resourceScope = resourceScope;
        _entryList.SizeChanged += OnEntryListSizeChanged;
    }

    internal void Attach(FilesColumnViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is not null)
            _viewModel.AutoColumnWidthsRequested -= OnAutoColumnWidthsRequested;

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.AutoColumnWidthsRequested += OnAutoColumnWidthsRequested;
            QueueAutoColumnWidths();
        }
    }

    internal void OnColumnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || GripColumn(sender) is not { } key)
            return;
        AutoFitColumn(key);
        e.Handled = true;
    }

    internal void OnColumnGripDragStarted(object sender, DragStartedEventArgs e)
        => _viewModel?.BeginColumnWidthDrag();

    internal void OnColumnGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (GripColumn(sender) is not { } key || _viewModel is not { } vm)
            return;
        vm.SetColumnWidth(key, vm.ColumnWidth(key) + e.HorizontalChange);
    }

    internal void OnColumnGripDragCompleted(object sender, DragCompletedEventArgs e)
        => _viewModel?.EndColumnWidthDrag();

    internal void OnDisplayModeChanged()
    {
        if (_viewModel is not { ColumnWidthsAreAuto: true })
            return;

        // 列の出入り後は、隠れていた列の設定幅を実測値として再利用しない。
        _contentWidths = null;
        QueueAutoColumnWidths();
    }

    internal void OnFolderChanged()
    {
        // 移動前に予約した Loaded 処理が、移動先の空一覧／古い一覧へ幅を当てないようにする。
        _autoWidthGeneration++;
        _autoWidthsQueued = false;
        _contentWidths = null;
    }

    private static FilesColumnKey? GripColumn(object sender)
        => sender is Thumb { Tag: string tag } && Enum.TryParse<FilesColumnKey>(tag, out var key)
            ? key
            : null;

    internal void AutoFitColumn(FilesColumnKey key)
    {
        if (_viewModel is not { } vm)
            return;
        vm.SetColumnWidth(key, ContentWidth(key));
        vm.EndColumnWidthDrag();
    }

    internal void ApplyAutoColumnWidths()
    {
        if (_viewModel is not { ColumnWidthsAreAuto: true } vm)
            return;

        // 中身の幅は、フォルダーや表示列が変わるまで計測結果を再利用する。
        var content = _contentWidths ??= vm.ColumnSettings.ToDictionary(
            setting => setting.Key,
            setting => setting.IsVisible ? ContentWidth(setting.Key) : setting.Width);

        var inset = _resourceScope.TryFindResource("FilesRowInset") is Thickness rowInset
            ? rowInset.Right
            : 0;
        var available = _entryList.ActualWidth - inset;
        vm.ApplyAutoColumnWidths(FilesColumnWidthPolicy.ComputeAutoWidths(
            content, vm.ColumnSettings, available));
    }

    private double ContentWidth(FilesColumnKey key)
    {
        if (_viewModel is not { } vm)
            return 0;

        var headerSize = FontSizeResource("Fs11", 11);
        var nameSize = FontSizeResource("Fs12", 12);
        var text = new FilesColumnTextMeasure(
            _resourceScope.FontFamily, FontStyles.Normal, FontWeights.Normal, _resourceScope);
        var badge = new FilesColumnTextMeasure(
            new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, _resourceScope);
        var setting = vm.ColumnSettings.FirstOrDefault(candidate => candidate.Key == key);
        return FilesColumnContentWidthPolicy.Measure(
            key, setting?.Label, vm.EntriesView.Cast<FileEntryViewModel>(),
            headerSize, nameSize, text, badge);
    }

    private double FontSizeResource(string key, double fallback)
        => _resourceScope.TryFindResource(key) is double size && size > 0 ? size : fallback;

    private void OnAutoColumnWidthsRequested(object? sender, EventArgs e)
    {
        _contentWidths = null;
        // 一覧の反映直後に幅を決める。Loaded まで遅らせると、非同期のフォルダー移動では
        // 「既定幅で一覧を描く → 後から自動幅へ変える」の二段階になり、列が大きく跳ねる。
        // まだビューの幅が決まっていない初回だけ、サイズ確定後へ回す。
        if (_entryList.ActualWidth > 0)
            ApplyAutoColumnWidths();
        else
            QueueAutoColumnWidths();
    }

    private void QueueAutoColumnWidths()
    {
        if (_autoWidthsQueued)
            return;
        _autoWidthsQueued = true;
        var generation = _autoWidthGeneration;
        _entryList.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != _autoWidthGeneration)
                return;
            _autoWidthsQueued = false;
            ApplyAutoColumnWidths();
        }), DispatcherPriority.Loaded);
    }

    private void OnEntryListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && _viewModel is { ColumnWidthsAreAuto: true })
            QueueAutoColumnWidths();
    }
}
