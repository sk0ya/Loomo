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
            EncodingInspectionPalette.Build(report, path, tab.Control.IsModified, JumpToPaletteTarget,
                conversion => () => _ = ConvertFileEncodingAsync(path, conversion)));
    }

    /// <summary>
    /// ディスク上のファイルの文字コード・改行を変換し、開いているタブを読み直す。未保存の変更があるタブがあれば
    /// 変換しない（読み直しで編集を捨てることになるので、保存してからにしてもらう）。変換前の本文はローカル履歴へ。
    /// エディタは読み込むたびに文字コードと改行の形式を判定し直すので、読み直した後の保存も変換後の形を保つ。
    /// </summary>
    private async Task ConvertFileEncodingAsync(string path, TextConversion conversion)
    {
        if (_editorTabs.Any(t => t.IsRealized && t.Control.IsModified
                && FilePathRelations.AreEqual(t.PeekFilePath, path)))
        {
            ShowRefactorStatus("文字コード：未保存の変更があります。保存してから変換してください。");
            return;
        }

        // 読み・書きは UI スレッドから外す。履歴への記録はほかの記録点（開いた・保存した）と同じく UI スレッドから。
        if (await Task.Run(() => TextFileEncoding.ReadFile(path)) is not { } decoded)
        {
            ShowRefactorStatus("文字コード：変換できませんでした（文字コードを判定できません）。");
            return;
        }
        _localHistory.RecordContent(path, decoded.Text, LocalHistoryKind.BeforeConvert);

        var (format, text) = TextFileEncoding.Apply(decoded, conversion);
        var error = await Task.Run(() =>
        {
            try { return TextFileEncoding.TryWriteFile(path, format, text) ? null : "その文字コードで表せない文字があります"; }
            catch (Exception ex) { return ex.Message; }
        });
        if (error is not null)
        {
            ShowRefactorStatus($"文字コード：変換できませんでした（{error}）。");
            return;
        }

        await ReloadEditorTabsFromDiskAsync([path]);
        ShowRefactorStatus($"{ConversionDoneLabel(conversion)}（変換前の本文はローカル履歴に残しています）。");
    }

    private static string ConversionDoneLabel(TextConversion conversion) => conversion switch
    {
        TextConversion.ToUtf8 => "UTF-8（BOM なし）に変換しました",
        TextConversion.ToUtf8WithBom => "UTF-8（BOM 付き）に変換しました",
        TextConversion.ToLf => "改行を LF にそろえました",
        _ => "改行を CRLF にそろえました",
    };
}
