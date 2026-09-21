using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧のWindows Shell操作と、ZIP／プロパティ／クイックアクセスの非同期ライフサイクル。</summary>
internal sealed class FilesColumnShellInteractionController
{
    private readonly Func<FilesColumnViewModel?> _getViewModel;
    private readonly Func<IReadOnlyList<FileEntryViewModel>> _getSelection;
    private readonly Func<Window?> _getOwnerWindow;
    private readonly Func<bool> _isLoaded;
    private readonly Action<string> _selectPath;
    private readonly Action<string> _showError;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _propertiesLoad;
    private CancellationTokenSource? _zipOperation;

    internal FilesColumnShellInteractionController(
        Func<FilesColumnViewModel?> getViewModel,
        Func<IReadOnlyList<FileEntryViewModel>> getSelection,
        Func<Window?> getOwnerWindow,
        Func<bool> isLoaded,
        Action<string> selectPath,
        Action<string> showError,
        Dispatcher dispatcher)
    {
        _getViewModel = getViewModel;
        _getSelection = getSelection;
        _getOwnerWindow = getOwnerWindow;
        _isLoaded = isLoaded;
        _selectPath = selectPath;
        _showError = showError;
        _dispatcher = dispatcher;
    }

    internal void CancelPending()
    {
        _propertiesLoad?.Cancel();
        _zipOperation?.Cancel();
    }

    internal void ExecuteShellAction(ShellFileAction action)
    {
        if (_getViewModel() is not { } vm)
            return;
        var paths = _getSelection().Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        var result = vm.ShellOperations.Execute(action, paths);
        if (!result.IsCancelled && result.FailedPaths.Count > 0)
            _showError(result.ErrorMessage ?? "Shell 操作を実行できませんでした。");
    }

    internal async Task CompressSelectionAsync()
    {
        if (_zipOperation is not null || _getViewModel() is not { } vm)
            return;

        var entries = _getSelection();
        if (entries.Count == 0)
            return;

        using var operation = new CancellationTokenSource();
        _zipOperation = operation;
        try
        {
            var archive = await vm.CompressEntriesAsync(entries, operation.Token);
            // 一覧を読み直してから、できた ZIP の行を選ぶ（行の実体化はレイアウト後）。
            vm.RefreshCommand.Execute(null);
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _selectPath(archive)));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // ビューがアンロードされた場合は、作成途中の一時 ZIP を残さず静かに終了する。
        }
        catch (Exception ex)
        {
            _showError($"ZIP を作成できませんでした: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_zipOperation, operation))
                _zipOperation = null;
        }
    }

    internal Task PinToQuickAccessAsync()
        => SetQuickAccessPinnedAsync(pin: true);

    internal Task UnpinFromQuickAccessAsync()
        => SetQuickAccessPinnedAsync(pin: false);

    private async Task SetQuickAccessPinnedAsync(bool pin)
    {
        var vm = _getViewModel();
        if (vm is null)
            return;
        var selection = _getSelection();
        await RunQuickAccessAsync(
            () => pin ? vm.PinToQuickAccessAsync(selection) : vm.UnpinFromQuickAccessAsync(selection),
            vm,
            pin ? "クイックアクセスへのピン留めに失敗しました。" : "クイックアクセスからの解除に失敗しました。");
    }

    private async Task RunQuickAccessAsync(
        Func<Task<QuickAccessBatchResult>> operation,
        FilesColumnViewModel vm,
        string failureMessage)
    {
        Mouse.OverrideCursor = Cursors.AppStarting;
        QuickAccessBatchResult? result = null;
        try
        {
            result = await operation();
        }
        catch (Exception)
        {
            // Explorer に聞けない環境では静かに何もしない。
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        vm.InvalidateQuickAccessCache();
        if (result is { HasFailures: true })
            _showError(result.ErrorMessage ?? failureMessage);
    }

    internal async Task ShowPropertiesAsync()
    {
        var selected = _getSelection();
        var vm = _getViewModel();
        if (selected.Count == 0 || _propertiesLoad is not null || vm is null)
            return;

        var targets = selected
            .Select(entry => new FilePropertiesTarget(entry.FullPath, entry.IsDirectory))
            .ToArray();

        using var operation = new CancellationTokenSource();
        _propertiesLoad = operation;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // フォルダーサイズや ACL 読み取りで UI を固めない。
            var result = await Task.Run(
                () => vm.FileProperties.ReadMany(targets, operation.Token), operation.Token);
            if (operation.IsCancellationRequested || !_isLoaded())
                return;
            var dialog = new FilePropertiesWindow(result) { Owner = _getOwnerWindow() };
            dialog.ShowDialog();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // ビューがアンロードされた、または読み取りがキャンセルされた場合は何もしない。
        }
        catch (Exception ex)
        {
            _showError($"プロパティを表示できませんでした: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_propertiesLoad, operation))
            {
                _propertiesLoad = null;
                Mouse.OverrideCursor = null;
            }
        }
    }
}
