using System;
using System.IO;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport の HTML プレビュー出力（Markdown/JSON プレビュー等）を、Loomo の仮想ホスト
/// （<see cref="MarkdownRenderer.AssetsVirtualHost"/> / <see cref="MarkdownRenderer.PreviewVirtualHost"/>）に
/// 依存しない「単体で開ける」HTML へ変換する（エクスポート用）。ペイン内の WebView2 でしか解決できない
/// 仮想ホスト URL を、素のブラウザでも開ける形へ書き換える純ロジック（テスト可能）。
/// <list type="bullet">
/// <item>mermaid / marp のランタイム JS（assets.loomo 配信）を、参照している文書に限りバンドル済み実ファイルの
///   <c>data:</c> URI へ差し替える（動的ローダはそのまま働き、テーマ初期化も従来経路で走る）。</item>
/// <item>相対パス画像の <c>&lt;base href&gt;</c>（preview.loomo）を、ソースファイルのフォルダの
///   <c>file://</c> URI へ書き換える（同一マシンで開けば画像も解決できる）。</item>
/// </list>
/// 画像自体の埋め込みはしない（大きくなりがちなので file:// 参照のまま）。
/// </summary>
public static class PortableHtml
{
    /// <param name="html">プレビュー提供者が生成したフル HTML。</param>
    /// <param name="sourceFileDir">相対画像解決の基準にするソースファイルのフォルダ（無ければ base 書き換えを省略）。</param>
    /// <param name="assetsDir">mermaid.min.js / marp.min.js が置かれた同梱アセットフォルダ。</param>
    public static string Build(string html, string? sourceFileDir, string assetsDir)
    {
        html = RewriteBaseHref(html, sourceFileDir);
        html = InlineAssets(html, assetsDir);
        return html;
    }

    /// <summary>
    /// <c>&lt;base href="https://preview.loomo/…"&gt;</c> を、ソースフォルダの file:// URI へ書き換える。
    /// base タグが無い（JSON プレビュー等）／フォルダ不明のときは何もしない。
    /// </summary>
    private static string RewriteBaseHref(string html, string? sourceFileDir)
    {
        if (string.IsNullOrEmpty(sourceFileDir) || !Directory.Exists(sourceFileDir))
            return html;

        var previewBase = Regex.Escape($"https://{MarkdownRenderer.PreviewVirtualHost}/");
        var pattern = "<base href=\"" + previewBase + "[^\"]*\">";
        if (!Regex.IsMatch(html, pattern))
            return html;

        // 末尾セパレータを付けてディレクトリ URI（file:///C:/proj/docs/）にする＝相対パスの基準になる。
        var dirUri = new Uri(sourceFileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar).AbsoluteUri;
        var replacement = "<base href=\"" + MarkdownRenderer.EncodeAttribute(dirUri) + "\">";
        return Regex.Replace(html, pattern, replacement);
    }

    /// <summary>
    /// mermaid / marp のランタイム URL（assets.loomo 配信）を、その文書が実際に参照しているときだけ
    /// バンドル済み実ファイルの data: URI へ差し替える。参照が無ければ差し替えず、巨大 JS の埋め込みを避ける。
    /// </summary>
    private static string InlineAssets(string html, string assetsDir)
    {
        // 本文に mermaid 図（<pre class="mermaid">）があるときだけ mermaid をバンドルする。
        if (html.Contains("class=\"mermaid\"", StringComparison.Ordinal))
            html = ReplaceAssetUrl(html, assetsDir, "mermaid.min.js");

        // marp 文書（ページに生 Markdown を焼き込む window.__marpSrc がある）ときだけ marp をバンドルする。
        if (html.Contains("window.__marpSrc", StringComparison.Ordinal))
            html = ReplaceAssetUrl(html, assetsDir, "marp.min.js");

        return html;
    }

    private static string ReplaceAssetUrl(string html, string assetsDir, string fileName)
    {
        var path = Path.Combine(assetsDir, fileName);
        if (!File.Exists(path))
            return html; // アセットが無ければ URL のまま（素のブラウザでは図が出ないだけ）。

        var url = $"https://{MarkdownRenderer.AssetsVirtualHost}/{fileName}";
        var dataUri = "data:text/javascript;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        return html.Replace(url, dataUri, StringComparison.Ordinal);
    }
}
