using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの Windows シェル連携（アプリで開く／共有／送る／ZIP／プロパティ／
/// クイックアクセス）。
///
/// <para>これらはツリー（<see cref="FolderTreeView"/>）にだけ入っていたが、エクスプローラー相当を
/// 名乗るのはこのファイル一覧の側で、しかも複数選択・並べ替え・表示形式を持つぶん、実際に
/// 「まとめて ZIP」「まとめてプロパティ」を使いたいのはこちらになる。実体（Shell アダプター・
/// プロパティ読み取り・クイックアクセス）はツリーと同じ DI インスタンスへ委譲する。</para></summary>
public partial class FilesColumnView
{
    private CancellationTokenSource? _propertiesLoadCts;
    private CancellationTokenSource? _zipOperationCts;

    private void OnOpenWithAppClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.OpenWith);

    private void OnShareClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.Share);

    private void OnSendToClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.SendTo);

    private void ExecuteShellAction(ShellFileAction action)
    {
        if (Vm is null)
            return;
        var paths = Selection().Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        var result = Vm.ShellOperations.Execute(action, paths);
        if (!result.IsCancelled && result.FailedPaths.Count > 0)
            ShowError(result.ErrorMessage ?? "Shell 操作を実行できませんでした。");
    }

    private async void OnCompressToZipClick(object sender, RoutedEventArgs e)
    {
        if (_zipOperationCts is not null || Vm is null)
            return;

        var entries = Selection();
        if (entries.Count == 0)
            return;

        using var cts = new CancellationTokenSource();
        _zipOperationCts = cts;
        try
        {
            var archive = await Vm.CompressEntriesAsync(entries, cts.Token);
            // 一覧を読み直してから、できた ZIP の行を選ぶ（行の実体化はレイアウト後）。
            Vm.RefreshCommand.Execute(null);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SelectPath(archive)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // ビューがアンロードされた場合は、作成途中の一時 ZIP を残さず静かに終了する。
        }
        catch (Exception ex)
        {
            ShowError($"ZIP を作成できませんでした: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_zipOperationCts, cts))
                _zipOperationCts = null;
        }
    }

    private void OnQuickAccessPinClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
            return;
        var result = Vm.PinToQuickAccess(Selection());
        Vm.InvalidateQuickAccessCache();
        if (result.HasFailures)
            ShowError(result.ErrorMessage ?? "クイックアクセスへのピン留めに失敗しました。");
    }

    private void OnQuickAccessUnpinClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
            return;
        var result = Vm.UnpinFromQuickAccess(Selection());
        Vm.InvalidateQuickAccessCache();
        if (result.HasFailures)
            ShowError(result.ErrorMessage ?? "クイックアクセスからの解除に失敗しました。");
    }

    private void OnPropertiesClick(object sender, RoutedEventArgs e) => ShowProperties();

    /// <summary>選択中の項目をまとめてプロパティウィンドウへ渡す（Alt+Enter・右クリック）。</summary>
    private async void ShowProperties()
    {
        var selected = Selection();
        // Vm は DataContext（依存関係プロパティ）を読むので、UI スレッドの外では触れない。
        // 下の Task.Run のラムダから参照すると、そこで初めて評価されて例外になる。
        var vm = Vm;
        if (selected.Count == 0 || _propertiesLoadCts is not null || vm is null)
            return;

        var targets = selected
            .Select(entry => new FilePropertiesTarget(entry.FullPath, entry.IsDirectory))
            .ToArray();

        using var cts = new CancellationTokenSource();
        _propertiesLoadCts = cts;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // フォルダーのサイズ計算やネットワーク／長い UNC パスの ACL 読み取りで UI を固めない。
            var result = await Task.Run(() => vm.FileProperties.ReadMany(targets, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !IsLoaded)
                return;
            var dialog = new FilePropertiesWindow(result) { Owner = OwnerWindow };
            dialog.ShowDialog();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // ビューがアンロードされた、または読み取りがキャンセルされた場合は何もしない。
        }
        catch (Exception ex)
        {
            ShowError($"プロパティを表示できませんでした: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_propertiesLoadCts, cts))
            {
                _propertiesLoadCts = null;
                Mouse.OverrideCursor = null;
            }
        }
    }
}
