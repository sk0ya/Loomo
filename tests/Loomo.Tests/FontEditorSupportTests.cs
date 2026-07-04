using System;
using System.IO;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// フォントの字形サンプル（specimen）プレビュー（<see cref="FontEditorSupport"/>）の検証。
/// 拡張子→提供者の解決、本文非依存（<see cref="IEditorSupportProvider.UsesEditorText"/> = false）、
/// フォントを data URI で @font-face へ埋め込んだ自己完結 HTML を返すこと、失敗時に例外を投げず
/// 案内ページを返すことを確かめる。実フォントは Windows 同梱のものを使い、無ければダミーで代替する
/// （プラットフォーム非依存）。
/// </summary>
public class FontEditorSupportTests
{
    private static FontEditorSupport Create() => new(new AiSettings());

    private static EditorSupportRegistry CreateRegistry()
        => new(new IEditorSupportProvider[] { Create() });

    [Theory]
    [InlineData(@"C:\work\font.ttf")]
    [InlineData(@"C:\work\font.otf")]
    [InlineData(@"C:\work\font.woff")]
    [InlineData(@"C:\work\font.woff2")]
    [InlineData(@"C:\work\FONT.TTF")]
    [InlineData(@"C:\work\FONT.WOFF2")]
    public void Resolve_フォントファイルにはFontプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<FontEditorSupport>(provider);
    }

    [Fact]
    public void UsesEditorTextはfalse_本文非依存かつHTML提供者()
    {
        var support = Create();

        Assert.IsAssignableFrom<IEditorSupportHtmlProvider>(support);
        Assert.False(support.UsesEditorText);
    }

    [Fact]
    public void DescribeTitle_Fontプレフィックスとファイル名()
    {
        Assert.Equal("Font: Roboto.ttf", Create().DescribeTitle(@"C:\work\Roboto.ttf"));
    }

    [Fact]
    public void RenderHtml_実フォントを字形サンプルのHTMLへ埋め込む()
    {
        // Windows 同梱フォントが読める環境では、それを使って本格的に検証する。
        var fontPath = FindWindowsTtf();
        if (fontPath is null)
        {
            // 非 Windows など同梱フォントが無い環境ではスキップ扱い（別テストで最低限を担保）。
            return;
        }

        var html = Create().RenderHtml(fontPath, text: "");

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("Font: " + Path.GetFileName(fontPath), html);       // <title>
        Assert.Contains("@font-face", html);
        Assert.Contains("data:font/ttf;base64,", html);                     // data URI 埋め込み
        Assert.Contains("format('truetype')", html);
        Assert.Contains("The quick brown fox", html);                        // パングラム
    }

    [Fact]
    public void RenderHtml_ダミーフォントでも例外を投げずHTMLを返す()
    {
        // 実フォントの無い環境向けのフォールバック検証：中身が不正でも例外を投げず、
        // 何らかの HTML（@font-face か エラーページ）を返す。
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ttf");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        try
        {
            var html = Create().RenderHtml(path, text: "");

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.True(
                html.Contains("@font-face") || html.Contains("表示できませんでした"),
                "字形サンプルかエラーページのいずれかを返すこと");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RenderHtml_存在しないパスはエラーページを返し例外を投げない()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ttf");   // 作らない

        var html = Create().RenderHtml(path, text: "");

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("表示できませんでした", html);
    }

    // Windows 同梱の TTF を探す（segoeui があれば優先、無ければ arial）。見つからなければ null。
    private static string? FindWindowsTtf()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrEmpty(fontsDir))
            return null;

        foreach (var name in new[] { "segoeui.ttf", "arial.ttf" })
        {
            var candidate = Path.Combine(fontsDir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
