using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 文字コードを調べる（いまのファイルの文字コード・BOM・改行・見えない文字・文字化けの跡）。
/// 結果はパレットに並べ、見つけたものを選ぶと右の欄にその行が出て、Enter でそこへ飛ぶ（ローカル履歴と同じ流儀）。
/// 文字コード・BOM・改行はファイルの性質なので<b>ディスク上の内容</b>を調べる。
/// </summary>
public partial class ShellWindow
{
    /// <summary>調べるファイルの大きさの上限（パレットで扱える範囲。これを超えるものは調べない）。</summary>
    private const long MaxEncodingInspectionBytes = 32L * 1024 * 1024;

    private void InspectActiveFileEncoding() => _ = InspectActiveFileEncodingAsync();

    private async Task InspectActiveFileEncodingAsync()
    {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path
            || tab.Control.IsVirtualDocument)
        {
            ShowRefactorStatus("文字コード：ファイルを開いているエディタがありません。");
            return;
        }

        EncodingReport report;
        try
        {
            // 読み込みと走査は UI スレッドから外す（大きなファイルでも画面を止めない）。
            report = await Task.Run(() =>
            {
                var info = new FileInfo(path);
                if (info.Length > MaxEncodingInspectionBytes)
                    throw new IOException($"{info.Length / 1024 / 1024}MB あり、大きすぎます");
                return EncodingInspector.Inspect(File.ReadAllBytes(path));
            });
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"文字コードを調べられませんでした: {ex.Message}");
            return;
        }

        OpenCommandPalette(PaletteMode.Command,
            EncodingInspectionPalette.Build(report, path, tab.Control.IsModified, JumpToPaletteTarget));
    }
}
