using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 出力の見せ直し（§39.5.1）の頭に、その回の画像を並べる（§39.10.2・§39.10.3）：端末に届いたインライン画像と、出力に名前が
/// 出てきた画像ファイル。ターミナルでは前者は文字に重なって描かれ（画像はカーソルを動かさない）、常駐セッションへ繋ぎ直すと
/// 消える。後者は名前の文字列でしかない。横の面では、どちらも同じ大きさの絵として並べる。
/// 裏のスレッドで呼ぶ（画像を符号化し、ファイルを写す）。
/// </summary>
internal static class TerminalOutputImagePresentation
{
    public static TerminalOutputPresentation With(
        TerminalOutputPresentation presentation, TerminalCommandOutput output, TerminalSupportImageStore? store)
    {
        if (store is null)
            return presentation;
        var inline = new List<TerminalSupportImage>();
        foreach (var image in (output.Images ?? []).Take(TerminalOutputImages.MaxImages))
        {
            byte[] data;
            try { data = image.Encoded(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException) { continue; }
            // Kitty の「置き直し」で同じ画像が何度も届く。置き場の名前は中身の指紋なので、同じ URL は1枚にする。
            if (store.ForBytes(data, image.MimeType) is { } url && inline.All(i => i.Url != url))
                inline.Add(new TerminalSupportImage(url, Caption: Caption(image)));
        }
        var files = TerminalOutputImages.FindPaths(output.Text, output.WorkingDirectory, File.Exists)
            .Select(path => store.ForFile(path) is { } url ? new TerminalSupportImage(url, FilePath: path) : null)
            .OfType<TerminalSupportImage>()
            .ToList();
        if (inline.Count == 0 && files.Count == 0)
            return presentation;
        var strips = TerminalOutputImages.Strip(inline, "端末に出た画像") + TerminalOutputImages.Strip(files, "出力に出てきた画像");
        return presentation with { Body = strips + presentation.Body };
    }

    private static string Caption(TerminalInlineImage image)
    {
        var protocol = image.Protocol switch { "sixel" => "Sixel", "iterm2" => "iTerm2", "kitty" => "Kitty", var p => p };
        return image.PixelWidth > 0 && image.PixelHeight > 0 ? $"{protocol} · {image.PixelWidth}×{image.PixelHeight}" : protocol;
    }
}
