
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: EditorSupport ペインの「エクスポート」（HTML/PDF 保存）。ヘッダーのエクスポート
/// ボタン → ドロップダウンから、現在のプレビューをファイルへ書き出す。HTML は仮想ホスト依存を外して
/// 単体で開ける形へ（<see cref="PortableHtml"/>）、PDF はペインの WebView2 の表示内容をそのまま
/// （WYSIWYG・プレビューのテーマ反映）<c>PrintToPdfAsync</c> で出力する。
/// </summary>
public partial class ShellWindow
{
    private void OnExportEditorSupportClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            var filePath = _editorSupport.Source?.Control.FilePath;
            ExportMarkdownMenuItem.IsEnabled = filePath is not null
                && _editorSupports.Resolve(filePath) is IEditorSupportMarkdownExportProvider;

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void OnExportEditorSupportHtml(object sender, RoutedEventArgs e)
    {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;
        if (_editorSupports.Resolve(filePath) is not IEditorSupportHtmlProvider htmlProvider)
            return; // ビジュアル提供者（CSV/TSV 等）や URI 提供者・非対応ファイルは書き出す HTML が無い。

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "HTMLとして保存",
            Filter = "HTML ファイル (*.html)|*.html",
            FileName = Path.GetFileNameWithoutExtension(filePath) + ".html",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var text = source.Control.Text;
        var sourceDir = Path.GetDirectoryName(filePath);
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var target = dialog.FileName;
        try
        {
            var portable = await Task.Run(
                () => PortableHtml.Build(htmlProvider.RenderHtml(filePath, text), sourceDir, assetsDir));
            await File.WriteAllTextAsync(target, portable, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            ReportEditorSupportExportError("HTML", ex.Message);
        }
    }

    private async void OnExportEditorSupportPdf(object sender, RoutedEventArgs e)
    {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;
        if (_editorSupports.Resolve(filePath) is not IEditorSupportHtmlProvider)
            return;
        if (_editorSupport.WebView.View?.CoreWebView2 is not { } core)
            return; // まだ描画されていない（ペイン未表示）。

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "PDFとして保存",
            Filter = "PDF ファイル (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(filePath) + ".pdf",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var settings = core.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true; // プレビューの背景（テーマ色）ごと出す＝WYSIWYG。
            if (!await core.PrintToPdfAsync(dialog.FileName, settings))
                ReportEditorSupportExportError("PDF", "WebView2 が書き出しに失敗しました。");
        }
        catch (Exception ex)
        {
            ReportEditorSupportExportError("PDF", ex.Message);
        }
    }

    private async void OnExportEditorSupportMarkdown(object sender, RoutedEventArgs e)
    {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;
        if (_editorSupports.Resolve(filePath) is not IEditorSupportMarkdownExportProvider markdownProvider)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Markdownとして保存",
            Filter = "Markdown ファイル (*.md)|*.md",
            FileName = Path.GetFileNameWithoutExtension(filePath) + ".md",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var text = source.Control.Text;
        try
        {
            var markdown = await Task.Run(() => markdownProvider.RenderMarkdown(filePath, text));
            await File.WriteAllTextAsync(dialog.FileName, markdown, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            ReportEditorSupportExportError("Markdown", ex.Message);
        }
    }

    private void ReportEditorSupportExportError(string kind, string message)
        => MessageBox.Show(this, $"{kind} のエクスポートに失敗しました。\n{message}",
            "エクスポート", MessageBoxButton.OK, MessageBoxImage.Warning);
}
