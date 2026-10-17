using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 文字コードを調べる（いまのファイルの文字コード・BOM・改行・見えない文字・文字化けの跡）と、
/// 保存するときの改行・文字コードを変える。
/// <para>
/// 調べるのは<b>ディスク上の内容</b>（文字コード・BOM・改行はファイルの性質なので）。結果はパレットに並べ、
/// 見つけたものを選ぶと右の欄にその行が出て、Enter でそこへ飛ぶ（ローカル履歴と同じ流儀）。
/// </para>
/// <para>
/// 変えるのは<b>エディタ</b>（<c>VimEditorControl.SetFileFormat</c>／<c>SetFileEncoding</c>＝Vim の
/// <c>:set fileformat=</c>／<c>fileencoding=</c>）。改行・文字コードはエディタが読み込み時に判定して保存時に使うものなので、
/// ホストがディスクを書き換えて読み直させるのではなく、エディタに「次はこの形で保存する」と頼む。本文は変えず
/// 未保存になり、保存したときにその形で書き出される（未保存の編集があっても使える・保存の経路は1本のまま）。
/// </para>
/// </summary>
public partial class ShellWindow
{
    /// <summary>調べるファイルの大きさの上限（パレットで扱える範囲。これを超えるものは調べない）。</summary>
    private const long MaxEncodingInspectionBytes = 32L * 1024 * 1024;

    private void InspectActiveFileEncoding() => _ = InspectActiveFileEncodingAsync();

    /// <summary>エディタのフッターの改行・文字コードの表示を押せるようにする（押すと選択肢をパレットに出す）。
    /// フッターは全エディタで共有の1本（<c>EditorSharedStatusBar</c>）で、出しているのはアクティブなタブのファイル。</summary>
    private void WireEditorFooterEncodingEntry()
    {
        EditorSharedStatusBar.FileFormatClicked += (_, _) => OpenEditorFileFormatPalette(lineEndings: true);
        EditorSharedStatusBar.FileEncodingClicked += (_, _) => OpenEditorFileFormatPalette(lineEndings: false);
    }

    private async Task InspectActiveFileEncodingAsync()
    {
        if (ActiveFileEditorTab() is not { } tab)
            return;
        var path = tab.Control.FilePath!;

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
                conversion => () => ApplyConversion(tab, conversion)));
    }

    /// <summary>フッターから：保存するときの改行・文字コードの選択肢を、エディタのいまの値を基準に出す。</summary>
    private void OpenEditorFileFormatPalette(bool lineEndings)
    {
        if (ActiveFileEditorTab() is not { } tab)
            return;
        var control = tab.Control;
        OpenCommandPalette(PaletteMode.Command, lineEndings
            ? EditorFileFormatPalette.BuildLineEndings(control.FileFormat, format => () => SetEditorFileFormat(tab, format))
            : EditorFileFormatPalette.BuildEncodings(control.FileEncoding, encoding => () => SetEditorFileEncoding(tab, encoding)));
    }

    private EditorTab? ActiveFileEditorTab()
    {
        if (_activeEditorTab is { IsRealized: true } tab && tab.Control.FilePath is { Length: > 0 }
            && !tab.Control.IsVirtualDocument)
            return tab;
        ShowRefactorStatus("文字コード：ファイルを開いているエディタがありません。");
        return null;
    }

    private void ApplyConversion(EditorTab tab, TextConversion conversion)
    {
        switch (conversion)
        {
            case TextConversion.ToLf: SetEditorFileFormat(tab, "unix"); break;
            case TextConversion.ToCrLf: SetEditorFileFormat(tab, "dos"); break;
            case TextConversion.ToUtf8: SetEditorFileEncoding(tab, "utf-8"); break;
            case TextConversion.ToUtf8WithBom: SetEditorFileEncoding(tab, "utf-8-bom"); break;
        }
    }

    private void SetEditorFileFormat(EditorTab tab, string format)
    {
        if (!tab.Control.SetFileFormat(format))
            return;
        UpdateEditorTab(tab);
        ShowRefactorStatus(tab.Control.IsModified
            ? $"保存すると改行を {EditorFileFormatPalette.Label(format)} にそろえて書き出します（Ctrl+S）。"
            : $"改行をファイルと同じ {EditorFileFormatPalette.Label(format)} に戻しました。");
    }

    private void SetEditorFileEncoding(EditorTab tab, string encoding)
    {
        // エディタの保存は表せない文字を「?」に置き換えて黙って書くので、失われる文字があれば選ばせない。
        if (EditorFileFormatPalette.FirstUnrepresentable(tab.Control.Text, encoding) is { } lost)
        {
            ShowRefactorStatus($"文字コード：{EditorFileFormatPalette.Label(encoding)} では「{lost}」を表せないので変えませんでした。");
            return;
        }
        if (!tab.Control.SetFileEncoding(encoding))
            return;
        UpdateEditorTab(tab);
        ShowRefactorStatus(tab.Control.IsModified
            ? $"保存すると {EditorFileFormatPalette.Label(encoding)} で書き出します（Ctrl+S）。"
            : $"文字コードをファイルと同じ {EditorFileFormatPalette.Label(encoding)} に戻しました。");
    }
}
