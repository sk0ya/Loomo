using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧のWindows Shell操作イベント入口。</summary>
public partial class FilesColumnView
{
    private readonly FilesColumnShellInteractionController _shellInteraction;

    private void OnOpenWithAppClick(object sender, RoutedEventArgs e)
        => _shellInteraction.ExecuteShellAction(ShellFileAction.OpenWith);

    private void OnShareClick(object sender, RoutedEventArgs e)
        => _shellInteraction.ExecuteShellAction(ShellFileAction.Share);

    private void OnSendToClick(object sender, RoutedEventArgs e)
        => _shellInteraction.ExecuteShellAction(ShellFileAction.SendTo);

    private async void OnCompressToZipClick(object sender, RoutedEventArgs e)
        => await _shellInteraction.CompressSelectionAsync();

    private async void OnQuickAccessPinClick(object sender, RoutedEventArgs e)
        => await _shellInteraction.PinToQuickAccessAsync();

    private async void OnQuickAccessUnpinClick(object sender, RoutedEventArgs e)
        => await _shellInteraction.UnpinFromQuickAccessAsync();

    private void OnPropertiesClick(object sender, RoutedEventArgs e) => ShowProperties();

    /// <summary>Alt+Enter と右クリックから同じ非同期プロパティ表示へ入る。</summary>
    private async void ShowProperties()
        => await _shellInteraction.ShowPropertiesAsync();
}
