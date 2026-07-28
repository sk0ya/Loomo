using Editor.Controls.Themes;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class ShellAppearanceCoordinatorTests
{
    [Theory]
    [InlineData("dark")]
    [InlineData("NORD")]
    [InlineData("tokyonight")]
    [InlineData("onedark")]
    public void Known_editor_theme_names_are_resolved(string name)
        => Assert.NotSame(EditorTheme.Dracula, ShellAppearanceCoordinator.ResolveEditorTheme(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Unknown_editor_theme_uses_Dracula(string? name)
        => Assert.Same(EditorTheme.Dracula, ShellAppearanceCoordinator.ResolveEditorTheme(name));

    /// <summary>ライブラリに無い配色は Loomo 側（EditorThemePresets）で解決される。
    /// 未知名フォールバック（Dracula）と同じにならないこと＝実際にプリセットが引けていることを確認する。</summary>
    [Theory]
    [InlineData("SolarizedDark")]
    [InlineData("monokai")]
    [InlineData("gruvboxdark")]
    [InlineData("catppuccinmocha")]
    [InlineData("kanagawa")]
    [InlineData("rosepine")]
    [InlineData("everforestdark")]
    [InlineData("nightowl")]
    [InlineData("ayudark")]
    [InlineData("highcontrast")]
    [InlineData("light")]
    [InlineData("solarizedlight")]
    [InlineData("catppuccinlatte")]
    [InlineData("rosepinedawn")]
    [InlineData("gruvboxlight")]
    [InlineData("onelight")]
    public void Loomo_defined_editor_themes_are_resolved(string name)
    {
        var theme = ShellAppearanceCoordinator.ResolveEditorTheme(name);
        Assert.NotSame(EditorTheme.Dracula, theme);
        // 背景・文字色が既定のままなら組み立てに失敗している。
        Assert.NotEqual(theme.Background.ToString(), theme.Foreground.ToString());
    }

    /// <summary>設定パネルの選択肢がすべて別々の配色へ解決される（＝キーの綴り違いで既定へ落ちていない）こと。
    /// 背景だけでは内蔵 Dark と高コントラストがどちらも純黒で衝突するため、キーワード色も併せて見る。</summary>
    [Fact]
    public void Every_editor_theme_choice_resolves_to_its_own_theme()
    {
        var keys = new[]
        {
            "Dracula", "Dark", "Nord", "TokyoNight", "OneDark", "SolarizedDark", "Monokai",
            "GruvboxDark", "CatppuccinMocha", "Kanagawa", "RosePine", "EverforestDark", "NightOwl", "AyuDark",
            "HighContrast", "Light", "SolarizedLight", "CatppuccinLatte",
            "RosePineDawn", "GruvboxLight", "OneLight",
        };
        var colors = keys
            .Select(k => ShellAppearanceCoordinator.ResolveEditorTheme(k))
            .Select(t => $"{t.Background}/{t.TokenKeyword}")
            .ToList();
        Assert.Equal(keys.Length, colors.Distinct().Count());
    }
}
