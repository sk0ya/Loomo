using System.IO;
using Microsoft.Win32;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.Definitions;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 定義の持ち出し（§45.9 P5）。Viewer・見どころ・言いつけの定義を人が選んだフォルダーへ書き出し、別の PC で取り込む。
/// 許し・録った動き・盆は持ち出さない（<see cref="LoomoDefinitions"/>）。フォルダーは人が選ぶ（標準のフォルダー選択）。
/// </summary>
public partial class ShellWindow {
    private static string LoomoAppDataRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo");

    private void ExportLoomoDefinitions() {
        var dialog = new OpenFolderDialog {
            Title = "定義を書き出すフォルダーを選ぶ",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try {
            var counts = LoomoDefinitions.Export(LoomoAppDataRoot(), dialog.FolderName);
            ToastService.Info($"定義を書き出しました：Viewer {counts.Viewers}・見どころ {counts.Probes}・言いつけ {counts.Orders}。許しは含めていません");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ToastService.Error($"定義を書き出せませんでした：{ex.Message}");
        }
    }

    private void ImportLoomoDefinitions() {
        var dialog = new OpenFolderDialog {
            Title = "取り込む定義（書き出したフォルダー）を選ぶ",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try {
            var counts = LoomoDefinitions.Import(dialog.FolderName, LoomoAppDataRoot());
            ToastService.Info($"定義を取り込みました：Viewer {counts.Viewers}・見どころ {counts.Probes}・言いつけ {counts.Orders}。"
                + "Viewerと見どころは、この PC で一度許すまで走りません");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                        or System.Text.Json.JsonException or InvalidOperationException) {
            // 壊れた JSON（JsonException）や version の型違い（InvalidOperationException）もここで受けて、UI スレッドの未処理例外にしない。
            ToastService.Error($"定義を取り込めませんでした：{ex.Message}");
        }
    }
}
