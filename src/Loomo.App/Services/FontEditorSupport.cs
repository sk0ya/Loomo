using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// フォントファイル（.ttf / .otf / .woff / .woff2）の字形サンプル（specimen）プレビュー。フォントの
/// バイト列を base64 化して <c>@font-face</c> の <c>src: url(data:…)</c> へ直接埋め込むことで、外部ホストへ
/// フォントを配信せず自己完結でその書体を描く。エディタ本文は使わず、ファイルパスから直接読む
/// （<see cref="UsesEditorText"/> = false）。表示専用。参照実装は <see cref="WordEditorSupport"/>。
/// </summary>
public sealed class FontEditorSupport : IEditorSupportHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".ttf", ".otf", ".woff", ".woff2"];

    // base64 は元バイトの約1.33倍へ膨らむため、これを超えるフォントは埋め込まず案内だけ出す。
    private const long MaxEmbedBytes = 20L * 1024 * 1024;

    // specimen 内で使う CSS 上のフォント名（実ファイルの内部名に依存しない固定名）。
    private const string FontFamily = "LoomoFontPreview";

    public FontEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // フォントは ZIP/バイナリ。エディタ本文は使わず、ファイルパスから直接読む。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"Font: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
    {
        var theme = _settings.Appearance.MarkdownPreviewTheme;
        try
        {
            var (mime, format) = ResolveFormat(Path.GetExtension(filePath));

            // 埋め込むと base64 で約1.33倍になるので、巨大フォントは読み込まず案内ページを返す。
            var info = new FileInfo(filePath);
            if (info.Exists && info.Length > MaxEmbedBytes)
                return MarkdownPage.BuildPage(TooLargeBody(filePath, info.Length), DescribeTitle(filePath), theme);

            // ロックしないよう即読み。
            var bytes = File.ReadAllBytes(filePath);
            var base64 = Convert.ToBase64String(bytes);
            var body = BuildSpecimen(filePath, mime, format, base64);
            return MarkdownPage.BuildPage(body, DescribeTitle(filePath), theme);
        }
        catch (Exception ex)
        {
            return OfficePreview.ErrorPage(filePath, ex, theme);
        }
    }

    // 拡張子から data URI の MIME と @font-face の format() を決める。
    private static (string Mime, string Format) ResolveFormat(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".ttf" => ("font/ttf", "truetype"),
            ".otf" => ("font/otf", "opentype"),
            ".woff" => ("font/woff", "woff"),
            ".woff2" => ("font/woff2", "woff2"),
            _ => ("font/ttf", "truetype"),
        };

    // 大きすぎて埋め込みを省いたときの案内本文（テーマ付きページへ載せる）。
    private static string TooLargeBody(string filePath, long length)
    {
        var name = MarkdownRenderer.Encode(Path.GetFileName(filePath));
        var mb = length / (1024.0 * 1024.0);
        return OfficePreview.BodyStyle +
            $"<p class=\"office-empty\">{name}（{mb:0.0} MB）はファイルが大きいためプレビューを省略しました。</p>";
    }

    // 字形サンプルの本文を組み立てる。specimen のテキストは固定文言なのでエスケープ不要だが、
    // ファイル名（見出し）とグリフ一覧の記号は Encode する。
    private static string BuildSpecimen(string filePath, string mime, string format, string base64)
    {
        var name = MarkdownRenderer.Encode(Path.GetFileName(filePath));
        var sb = new StringBuilder();

        // @font-face と specimen 用スタイル。フォントは data URI で自己完結。
        sb.Append("<style>")
          .Append("@font-face { font-family: '").Append(FontFamily).Append("'; src: url(data:")
          .Append(mime).Append(";base64,").Append(base64).Append(") format('").Append(format).Append("'); }")
          .Append(".specimen { font-family: '").Append(FontFamily).Append("', 'Segoe UI', sans-serif; }")
          .Append(".fs-title { font-size: 1.1em; opacity: .7; margin-bottom: 16px; word-break: break-all; }")
          .Append(".fs-section { margin: 22px 0 8px; font-size: .85em; letter-spacing: .05em; text-transform: uppercase; opacity: .55; }")
          .Append(".fs-row { display: flex; align-items: baseline; gap: 14px; margin: 4px 0; }")
          .Append(".fs-px { flex: 0 0 3.2em; text-align: right; font-size: .75em; opacity: .5; font-family: 'Cascadia Code', Consolas, monospace; }")
          .Append(".fs-line { line-height: 1.25; word-break: break-word; }")
          .Append(".fs-para { margin: 6px 0; line-height: 1.4; }")
          .Append(".fs-glyphs { display: grid; grid-template-columns: repeat(auto-fill, minmax(2.2em, 1fr)); gap: 4px; margin: 8px 0; }")
          .Append(".fs-glyphs > span { text-align: center; padding: 6px 0; border: 1px solid rgba(128,128,128,.25); border-radius: 4px; font-size: 1.5em; }")
          .Append(".fs-hero { font-size: 96px; line-height: 1.1; margin: 8px 0; }")
          .Append("</style>");

        // 見出し（ファイル名）。
        sb.Append("<div class=\"fs-title\">").Append(name).Append("</div>");

        // サイズ段（size ladder）：同じパングラムを各サイズで並べる。
        const string ladderText = "The quick brown fox jumps over the lazy dog";
        sb.Append("<div class=\"fs-section\">Size ladder</div>");
        foreach (var px in new[] { 12, 16, 20, 24, 32, 48, 64 })
        {
            sb.Append("<div class=\"fs-row\"><span class=\"fs-px\">").Append(px).Append("px</span>")
              .Append("<span class=\"specimen fs-line\" style=\"font-size:").Append(px).Append("px\">")
              .Append(ladderText).Append("</span></div>");
        }

        // パングラム（英語＋日本語）。
        sb.Append("<div class=\"fs-section\">Pangrams</div>");
        foreach (var line in new[]
        {
            "The quick brown fox jumps over the lazy dog",
            "Sphinx of black quartz, judge my vow",
            "いろはにほへと ちりぬるを わかよたれそ つねならむ",
            "あのイーハトーヴォのすきとおった風、夏でも底に冷たさをもつ青いそら",
        })
        {
            sb.Append("<p class=\"specimen fs-para\" style=\"font-size:22px\">").Append(line).Append("</p>");
        }

        // グリフ一覧：A–Z / a–z / 0–9 / 代表的な記号。記号は Encode する。
        sb.Append("<div class=\"fs-section\">Glyphs</div><div class=\"specimen fs-glyphs\">");
        AppendGlyphRange(sb, 'A', 'Z');
        AppendGlyphRange(sb, 'a', 'z');
        AppendGlyphRange(sb, '0', '9');
        foreach (var ch in "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")
            sb.Append("<span>").Append(MarkdownRenderer.Encode(ch.ToString())).Append("</span>");
        sb.Append("</div>");

        // 大サイズの見本（欧文の "Ag" と日本語の "永"）。
        sb.Append("<div class=\"fs-section\">Sample</div>")
          .Append("<div class=\"specimen fs-hero\">Ag 永</div>");

        return sb.ToString();
    }

    private static void AppendGlyphRange(StringBuilder sb, char from, char to)
    {
        for (var ch = from; ch <= to; ch++)
            sb.Append("<span>").Append(ch).Append("</span>");
    }
}
