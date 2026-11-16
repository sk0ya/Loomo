using System;
using System.Collections.Generic;
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

/// <summary>自分のテーマ（<c>%APPDATA%/Loomo/themes/&lt;種類&gt;/*.json</c>）。テーマは種類ごと（アプリの画面・エディタ・
/// ターミナル・Markdown プレビュー）に別々に作る。どんな色を書かれても同梱テーマと同じ決まりを満たすこと、書き間違いは
/// 理由つきで弾くこと、選択欄の保存値 <c>user:&lt;id&gt;</c> がその種類の解決口にだけ届くことを押さえる。</summary>
public class UserThemeTests
{
    private static CustomTheme Read(CustomThemeKind kind, string json, string id = "t") =>
        CustomThemeFormat.Read(kind, id, $"{id}.json", json);

    [Theory]
    [InlineData(CustomThemeKind.App)]
    [InlineData(CustomThemeKind.Editor)]
    [InlineData(CustomThemeKind.Terminal)]
    [InlineData(CustomThemeKind.Preview)]
    public void 背景と文字だけで全部の欄が埋まる(CustomThemeKind kind)
    {
        var t = Read(kind, """{ "background": "#202020", "foreground": "#E0E0E0" }""");
        Assert.False(t.IsLight);
        Assert.Equal("t", t.Name);
        Assert.Equal(ThemeSchemas.For(kind).Slots.Select(s => s.Key).OrderBy(k => k), t.Colors.Keys.OrderBy(k => k));
        Assert.Equal(2, t.Explicit.Count);
    }

    [Fact]
    public void 欄の鍵は種類ごとに重ならず必須は背景と文字だけ()
    {
        foreach (var schema in ThemeSchemas.All)
        {
            Assert.Equal(schema.Slots.Count, schema.Slots.Select(s => s.Key).Distinct().Count());
            Assert.Equal(new[] { "background", "foreground" }, schema.Slots.Where(s => s.Required).Select(s => s.Key));
            Assert.All(schema.Slots, s => Assert.True(schema.GroupDescriptions.ContainsKey(s.Group), $"{schema.Title}: {s.Group} に説明が無い"));
        }
    }

    [Fact]
    public void コメントと末尾のカンマを許す()
    {
        var t = Read(CustomThemeKind.Editor, """
            {
              // 名前
              "name": "わたしの色",
              "background": "#FDF6E3", // 地
              "foreground": "#3C3836",
            }
            """);
        Assert.Equal("わたしの色", t.Name);
        Assert.True(t.IsLight);
    }

    [Theory]
    [InlineData(CustomThemeKind.App, """{ "foreground": "#FFFFFF" }""", "background")]
    [InlineData(CustomThemeKind.App, """{ "background": "#000", "foreground": "white" }""", "foreground")]
    [InlineData(CustomThemeKind.Editor, """{ "background": "#000", "foreground": "#FFF", "keywrd": "#111111" }""", "keywrd")]
    [InlineData(CustomThemeKind.Terminal, """{ "background": "#000", "foreground": "#FFF", "keyword": "#111111" }""", "keyword")]
    public void 書き間違いや別の種類の鍵は理由つきで弾く(CustomThemeKind kind, string json, string mention)
    {
        var ex = Assert.Throws<FormatException>(() => Read(kind, json));
        Assert.Contains(mention, ex.Message);
    }

    [Fact]
    public void 書いた色はそのまま使い書かない色は自動で決まる()
    {
        var t = Read(CustomThemeKind.Terminal, """{ "background": "#101010", "foreground": "#F0F0F0", "red": "#FF0000" }""");
        Assert.Equal(Color.FromRgb(0xFF, 0, 0), t["red"]);
        Assert.Equal(t["foreground"], t["cursor"]);                 // カーソルは自動なら文字と同じ
        Assert.NotEqual(t["red"], t["brightRed"]);                  // 明るい版は標準を明るくしたもの
        Assert.True(CustomThemeFormat.IsLightBackground(t["brightRed"]) || t["brightRed"].R > t["red"].R || t["brightRed"].G > 0);
    }

    /// <summary>アプリの画面のテーマ：暗い地・明るい地・中間の地と色々なアクセントで、作ったパレットが同梱テーマの
    /// 決まりを満たすこと（作った人が色の理屈を知らなくても崩れない）。</summary>
    [Theory]
    [InlineData("#000000", "#FFFFFF", "#FFC799")]
    [InlineData("#1B1E28", "#A6ACCD", "#5DE4C7")]
    [InlineData("#2E3440", "#D8DEE9", "#5E81AC")]
    [InlineData("#808080", "#000000", "#FF0000")]
    [InlineData("#FFFFFF", "#24292F", "#0969DA")]
    [InlineData("#F2ECBC", "#545464", "#FFAA33")]
    public void アプリの画面のパレットが同梱テーマと同じ決まりを満たす(string bg, string fg, string accent)
    {
        var t = Read(CustomThemeKind.App, $$"""{ "background": "{{bg}}", "foreground": "{{fg}}", "accent": "{{accent}}" }""");
        var p = AppPaletteBuilder.Build(t);

        Assert.Equal(PaletteKeys().OrderBy(k => k), p.Keys.OrderBy(k => k));
        Assert.Equal(ThemeManager.AccentForeground(p["Accent"]), p["AccentFg"]);
        Assert.True(ThemeManager.Contrast(p["Accent"], p["AccentFg"]) >= 4.5);
        // ペインヘッダーの帯は実行時（ThemeManager.ApplyPaneHeader）と同じ値。
        Assert.Equal(ThemeManager.PaneHeaderTint(p["Panel"], p["Accent"], p["FgDim"]), p["PaneHeaderBg"]);
        Assert.True(ThemeManager.Contrast(p["SelectionBg"], p["Fg"]) >= Math.Min(3.0, ThemeManager.Contrast(p["Bg"], p["Fg"])));
    }

    private static IEnumerable<string> PaletteKeys()
    {
        string[] keys =
        [
            "Bg", "BgAlt", "Panel", "ActivityBarBg", "PaneHeaderBg", "Border", "Accent", "AccentHover", "AccentFg",
            "Fg", "FgDim", "SearchHighlight", "SelectionBg", "Success", "Error",
            "SymNamespace", "SymType", "SymInterface", "SymEnum", "SymMethod", "SymProperty", "SymField", "SymConstant",
        ];
        return keys;
    }

    /// <summary>同梱のパレットのキーと、アプリの画面のテーマから作るパレットのキーが一対一（足りないと一部だけ既定に落ちる）。</summary>
    [Fact]
    public void アプリの画面のパレットは同梱のパレットとキーが一致する()
    {
        RunSta(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _ = Application.ResourceAssembly;
            var dark = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/sk0ya.Loomo.App;component/Themes/Palette.Dark.xaml", UriKind.Absolute)
            };
            var built = AppPaletteBuilder.Build(Read(CustomThemeKind.App, """{ "background": "#111111", "foreground": "#EEEEEE" }"""));
            Assert.Equal(dark.Keys.Cast<string>().OrderBy(k => k), built.Keys.OrderBy(k => k));
        });
    }

    [Fact]
    public void 保存値のuser鍵はその種類の解決口にだけ届く()
    {
        var folder = Path.Combine(Path.GetTempPath(), "loomo-user-themes-" + Guid.NewGuid().ToString("N"));
        var previous = UserThemes.Folder;
        try
        {
            UserThemes.Folder = folder;
            Directory.CreateDirectory(UserThemes.FolderFor(CustomThemeKind.Editor));
            Directory.CreateDirectory(UserThemes.FolderFor(CustomThemeKind.Terminal));
            Directory.CreateDirectory(UserThemes.FolderFor(CustomThemeKind.Preview));
            File.WriteAllText(Path.Combine(UserThemes.FolderFor(CustomThemeKind.Editor), "mine.json"),
                """{ "name": "Mine", "background": "#112233", "foreground": "#DDEEFF", "keyword": "#FF00AA" }""");
            File.WriteAllText(Path.Combine(UserThemes.FolderFor(CustomThemeKind.Terminal), "term.json"),
                """{ "background": "#010203", "foreground": "#F0F0F0", "red": "#AA0000" }""");
            File.WriteAllText(Path.Combine(UserThemes.FolderFor(CustomThemeKind.Preview), "doc.json"),
                """{ "background": "#FAFAFA", "foreground": "#222222" }""");
            File.WriteAllText(Path.Combine(UserThemes.FolderFor(CustomThemeKind.Editor), "broken.json"), """{ "background": "#000" """);
            UserThemes.Reload();

            Assert.Equal("Mine", Assert.Single(UserThemes.All(CustomThemeKind.Editor)).Name);
            Assert.Contains(UserThemes.Errors, e => e.StartsWith("editor/broken.json"));
            Assert.NotNull(UserThemes.TryGet(CustomThemeKind.Editor, "USER:mine"));
            Assert.Null(UserThemes.TryGet(CustomThemeKind.Terminal, "user:mine"));   // 種類が違えば見えない
            Assert.Null(UserThemes.TryGet(CustomThemeKind.Editor, "Dark"));

            var editor = ShellAppearanceCoordinator.ResolveEditorTheme("user:mine");
            Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xAA), ((SolidColorBrush)editor.TokenKeyword).Color);
            Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), ((SolidColorBrush)editor.Background).Color);

            var terminal = ShellAppearanceCoordinator.BuildTerminalColorTheme(" user:term ");   // 前後の空白は無視
            Assert.Equal(Color.FromRgb(0x01, 0x02, 0x03), terminal.Background);
            Assert.Equal(Color.FromRgb(0xAA, 0x00, 0x00), terminal.AnsiPalette[1]);

            Assert.Contains("#FAFAFA", MarkdownPage.BuildPage("<p>x</p>", "x.md", "user:doc"));

            // 前の形（themes 直下の部屋全体テーマ）は読まないが、黙って消さずに理由を出す。
            File.WriteAllText(Path.Combine(folder, "old.json"), """{ "background": "#000000", "foreground": "#FFFFFF" }""");
            UserThemes.Reload();
            Assert.Contains(UserThemes.Errors, e => e.StartsWith("old.json：前の形"));

            // 消えたテーマは既定へ落ちる（例外にならない）。
            Assert.Same(EditorTheme.Dracula, ShellAppearanceCoordinator.ResolveEditorTheme("user:gone"));
        }
        finally
        {
            UserThemes.Folder = previous;
            UserThemes.Reload();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>下書き：読んで書き戻すと、手で決めた色と自動の欄がそのまま残ること（自動の欄はファイルに書かない
    /// ＝ほかの色に追従し続ける）。</summary>
    [Fact]
    public void 下書きは読んで書き戻しても中身が変わらない()
    {
        var json = """{ "name": "わたしの色", "background": "#101820", "foreground": "#E0E6F0", "accent": "#FF8800", "keyword": "#C080FF" }""";
        var draft = CustomThemeDraft.FromJson(CustomThemeKind.Editor, json);
        Assert.Equal("わたしの色", draft.Name);
        Assert.Null(draft.Values["comment"]);
        Assert.Equal(Color.FromRgb(0xFF, 0x88, 0x00), draft.Values["accent"]);

        var again = CustomThemeDraft.FromJson(CustomThemeKind.Editor, draft.ToJson());
        Assert.Equal(draft.Name, again.Name);
        Assert.Equal(draft.Values, again.Values);
        Assert.Equal(Read(CustomThemeKind.Editor, json).Colors.OrderBy(p => p.Key), again.Derive().Colors.OrderBy(p => p.Key));
    }

    [Fact]
    public void 読みにくい組み合わせには注意が出る()
    {
        var readable = Read(CustomThemeKind.Editor, """{ "background": "#1E1E1E", "foreground": "#D4D4D4" }""");
        Assert.Empty(UserThemeEditorViewModel.Check(readable));

        var muddy = Read(CustomThemeKind.Editor, """{ "background": "#808080", "foreground": "#9A9A9A", "keyword": "#858585" }""");
        var warnings = UserThemeEditorViewModel.Check(muddy).ToList();
        Assert.Contains(warnings, w => w.Key == "foreground");
        Assert.Contains(warnings, w => w.Key == "keyword");
    }

    /// <summary>「〜をもとに新しく作る」の写しが、どの種類のどの同梱テーマからでも読めるテーマになり、
    /// 元の背景の明暗を保つこと。</summary>
    [Fact]
    public void 同梱テーマから写した下書きがどの種類でも読める()
    {
        RunSta(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _ = Application.ResourceAssembly;
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                foreach (var kind in Enum.GetValues<CustomThemeKind>())
                {
                    var seed = AppearanceViewModel.SeedFromBuiltIn(kind, theme.ToString());
                    var draft = new CustomThemeDraft(kind) { Name = $"{theme} をもとに" };
                    foreach (var (k, v) in seed) draft.Values[k] = v;
                    var t = CustomThemeDraft.FromJson(kind, draft.ToJson()).Derive();
                    Assert.Equal($"{theme} をもとに", t.Name);
                    if (kind == CustomThemeKind.App)
                        Assert.True(t.IsLight == theme.IsLight(), $"{theme}: 写しの明暗が元のテーマと違う");
                }
            }
        });
    }

    /// <summary>どの同梱テーマも、エディタ・プレビューで既定（Dracula）へ落ちずに自分の配色に解決されること
    /// （高コントラストのプレビューだけは専用が無いので Dark）。</summary>
    [Fact]
    public void どの同梱テーマも各所で自分の配色に解決される()
    {
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            var name = theme.ToString();
            if (theme is not AppTheme.Dracula)
                Assert.NotSame(EditorTheme.Dracula, ShellAppearanceCoordinator.ResolveEditorTheme(name));
            var preview = MarkdownPage.NormalizeStyle(name);
            Assert.True(theme is AppTheme.Dracula || preview != "Dracula", $"{theme}: プレビューが既定へ落ちた");
        }
    }

    /// <summary>8 桁の 16 進は #RRGGBBAA（VS Code・CSS）と #AARRGGBB（WPF）のどちらとも決められないので読まない
    /// （貼った色を黙って別の色にしない）。JSON では 6 桁で書くよう理由を出す。</summary>
    [Fact]
    public void 八桁の16進は読まずに理由を出す()
    {
        Assert.False(CustomThemeFormat.TryParse("#264F78CC", out _));
        Assert.True(CustomThemeFormat.TryParse(" #abc ", out var c));
        Assert.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), c);
        var ex = Assert.Throws<FormatException>(() =>
            Read(CustomThemeKind.Editor, """{ "background": "#264F78CC", "foreground": "#FFFFFF" }"""));
        Assert.Contains("6 桁", ex.Message);
    }

    [Fact]
    public void ANSIの鍵はターミナルの欄の並びどおり16色()
    {
        Assert.Equal(16, ThemeSchemas.AnsiKeys.Count);
        Assert.Equal("black", ThemeSchemas.AnsiKeys[0]);
        Assert.Equal("white", ThemeSchemas.AnsiKeys[7]);
        Assert.Equal("brightBlack", ThemeSchemas.AnsiKeys[8]);
        Assert.Equal("brightWhite", ThemeSchemas.AnsiKeys[15]);
    }

    /// <summary>ピッカーの HSV ⇄ RGB が往復で色を崩さないこと（16 進で打った色がそのまま戻る）。</summary>
    [Theory]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#808080")]
    [InlineData("#FF0000")]
    [InlineData("#5DE4C7")]
    [InlineData("#C4746E")]
    [InlineData("#1E66F5")]
    public void ピッカーのHSV変換は往復で色を崩さない(string hex)
    {
        Assert.True(CustomThemeFormat.TryParse(hex, out var c));
        var (h, s, v) = sk0ya.Loomo.App.Views.ColorPicker.ToHsv(c);
        Assert.Equal(c, sk0ya.Loomo.App.Views.ColorPicker.FromHsv(h, s, v));
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
