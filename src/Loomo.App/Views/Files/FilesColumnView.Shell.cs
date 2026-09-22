using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧のWindows Shell操作イベント入口。</summary>
public partial class FilesColumnView
{
    private readonly FilesColumnShellInteractionController _shellInteraction;

    private void OnOpenWithAppClick(object sender, RoutedEventArgs e)
        => _shellInteraction.ExecuteShellAction(ShellFileAction.OpenWith);

    /// <summary>Explorer のメニュー。「名前の変更」「削除」は Explorer のビュー前提なので、
    /// この一覧の同じ操作（履歴に積まれる）へ振り替える。</summary>
    private void OnExplorerMenuClick(object sender, RoutedEventArgs e)
        => _shellInteraction.ShowExplorerMenu((verb, targets) =>
        {
            switch (verb)
            {
                case "rename":
                    RenameEntry(targets.FirstOrDefault());
                    return true;
                case "delete":
                    DeleteEntries(targets);
                    return true;
                default:
                    return false;
            }
        });

    private async void OnCompressToZipClick(object sender, RoutedEventArgs e)
        => await _shellInteraction.CompressSelectionAsync();

    private async void OnQuickAccessPinClick(object sender, RoutedEventArgs e)
        => await _shellInteraction.PinToQuickAccessAsync();

    private async void OnQuickAccessUnpinClick(object sender, RoutedEventArgs e)
        => await _shellInteraction.UnpinFromQuickAccessAsync();

    /// <summary>Alt+Enter：Windows のプロパティ（Explorer と同じもの）。</summary>
    private void ShowProperties() => _shellInteraction.ShowProperties();
}
