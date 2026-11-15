using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Editor.Controls.Themes;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Tests;

/// <summary>自分のテーマ（<c>%APPDATA%/Loomo/themes/*.json</c>）。基準色の十数個から UI・エディタ・ターミナル・
/// プレビューの配色を派生させるので、どんな色を書かれても同梱テーマと同じ決まり（<see cref="PaletteResourceTests"/>）
/// を満たすこと、書き間違いは理由つきで弾くこと、選択肢の保存値 <c>user:&lt;id&gt;</c> が各解決口に届くことを押さえる。</summary>
public class UserThemeTests
{
    private static UserTheme Parse(string json, string id = "t") => UserThemeParser.Parse(id, $"{id}.json", json);

    [Fact]
    public void 地と文字だけで全部の色が埋まる()
    {
        var t = Parse("""{ "background": "#202020", "foreground": "#E0E0E0" }""");

        Assert.False(t.IsLight);
        Assert.Equal("t", t.Name);
        Assert.Equal(UserThemeParser.PaletteKeys.OrderBy(k => k), t.Ui.Keys.OrderBy(k => k));
        Assert.Equal(16, t.Ansi.Count);
        Assert.Equal(t.Blue, t.Accent);   // accent を書かなければ blue
    }

    [Fact]
    public void コメントと末尾のカンマを許す()
    {
        var t = Parse("""
            {
              // 名前
              "name": "わたしの色",
              "background": "#FDF6E3", // 地
              "foreground": "#3C3836",
              "ui": { },
            }
            """);
        Assert.Equal("わたしの色", t.Name);
        Assert.True(t.IsLight);
    }

    [Theory]
    [InlineData("""{ "foreground": "#FFFFFF" }""", "background")]
    [InlineData("""{ "background": "#000", "foreground": "white" }""", "foreground")]
    [InlineData("""{ "background": "#000", "foreground": "#FFF", "ui": { "Bgg": "#111111" } }""", "Bgg")]
    [InlineData("""{ "background": "#000", "foreground": "#FFF", "ansi": ["#000000"] }""", "16")]
    public void 書き間違いは理由つきで弾く(string json, string mention)
    {
        var ex = Assert.Throws<FormatException>(() => Parse(json));
        Assert.Contains(mention, ex.Message);
    }

    [Fact]
    public void uiとansiで直接上書きできる()
    {
        var ansi = string.Join(", ", Enumerable.Range(0, 16).Select(i => $"\"#{i:X2}{i:X2}{i:X2}\""));
        var t = Parse($$"""{ "background": "#101010", "foreground": "#F0F0F0", "ui": { "border": "#123456" }, "ansi": [{{ansi}}] }""");

        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), t.Ui["Border"]);
        Assert.Equal(Color.FromRgb(0x0F, 0x0F, 0x0F), t.Ansi[15]);
    }

    /// <summary>暗い地・明るい地・中間の地と、明るい／暗い／くすんだアクセントの組み合わせで、派生した
    /// パレットが同梱テーマの決まりを満たすこと（作った人が色の理屈を知らなくても崩れない）。</summary>
    [Theory]
    [InlineData("#000000", "#FFFFFF", "#FFC799")]
    [InlineData("#1B1E28", "#A6ACCD", "#5DE4C7")]
    [InlineData("#2E3440", "#D8DEE9", "#5E81AC")]
    [InlineData("#808080", "#000000", "#FF0000")]
    [InlineData("#FFFFFF", "#24292F", "#0969DA")]
    [InlineData("#F2ECBC", "#545464", "#FFAA33")]
    [InlineData("#FDF6E3", "#657B83", "#B58900")]
    public void 派生したパレットが同梱テーマと同じ決まりを満たす(string bg, string fg, string accent)
    {
        var t = Parse($$"""{ "background": "{{bg}}", "foreground": "{{fg}}", "accent": "{{accent}}" }""");
        Color C(string key) => t.Ui[key];

        Assert.Equal(ThemeManager.AccentForeground(C("Accent")), C("AccentFg"));
        Assert.True(ThemeManager.Contrast(C("Accent"), C("AccentFg")) >= 4.5);

        var header = ThemeManager.Contrast(C("Panel"), C("PaneHeaderBg"));
        Assert.InRange(header, 1.12, 1.26);
        static double L(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        Assert.Equal(!t.IsLight, L(C("PaneHeaderBg")) > L(C("Panel")));

        // 地と文字の差が小さいテーマ（中間の灰に黒）でも、選択行は文字との差を地より縮めない。
        Assert.True(ThemeManager.Contrast(C("SelectionBg"), C("Fg"))
            >= Math.Min(3.0, ThemeManager.Contrast(C("Bg"), C("Fg"))));
    }

    [Fact]
    public void 保存値のuser鍵がエディタとターミナルとプレビューに届く()
    {
        var folder = Path.Combine(Path.GetTempPath(), "loomo-user-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = UserThemes.Folder;
        try
        {
            File.WriteAllText(Path.Combine(folder, "mine.json"),
                """{ "name": "Mine", "background": "#112233", "foreground": "#DDEEFF", "keyword": "#FF00AA" }""");
            File.WriteAllText(Path.Combine(folder, "broken.json"), """{ "background": "#000" """);
            UserThemes.Folder = folder;
            UserThemes.Reload();

            Assert.Equal("Mine", Assert.Single(UserThemes.All).Name);
            Assert.Contains(UserThemes.Errors, e => e.StartsWith("broken.json"));
            Assert.NotNull(UserThemes.TryGet("USER:mine"));
            Assert.Null(UserThemes.TryGet("Dark"));

            var editor = ShellAppearanceCoordinator.ResolveEditorTheme("user:mine");
            Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xAA), ((SolidColorBrush)editor.TokenKeyword).Color);
            Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), ((SolidColorBrush)editor.Background).Color);

            var terminal = ShellAppearanceCoordinator.BuildTerminalColorTheme("user:mine");
            Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), terminal.Background);

            Assert.Equal("user:mine", MarkdownPage.NormalizeStyle("user:mine"));
            Assert.Contains("#112233", MarkdownPage.BuildPage("<p>x</p>", "x.md", "user:mine"));

            // 消えたテーマは既定へ落ちる（落ちずに例外にならない）。
            Assert.Same(EditorTheme.Dracula, ShellAppearanceCoordinator.ResolveEditorTheme("user:gone"));
        }
        finally
        {
            UserThemes.Folder = previous;
            UserThemes.Reload();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>「いまの配色から作る」の雛形が、どの同梱テーマからでも読めるテーマになること。</summary>
    [Fact]
    public void 同梱テーマから作った雛形がそのまま読める()
    {
        RunSta(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _ = Application.ResourceAssembly;
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                var json = AppearanceViewModel.SeedFromBuiltIn(theme, $"{theme} をもとに");
                var t = Parse(json, theme.ToString());
                Assert.Equal($"{theme} をもとに", t.Name);
                Assert.True(t.IsLight == theme.IsLight(), $"{theme}: 雛形の明暗が元のテーマと違う");
            }
        });
    }

    private static void RunSta(Action body)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { ex = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }
}
