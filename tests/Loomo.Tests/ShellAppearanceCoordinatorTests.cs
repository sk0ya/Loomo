using System.Runtime.CompilerServices;
using System.Windows.Media;
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
        Assert.NotEqual(Describe(theme.Background), Describe(theme.Foreground));
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
            .Select(t => $"{Describe(t.Background)}/{Describe(t.TokenKeyword)}")
            .ToList();
        Assert.Equal(keys.Length, colors.Distinct().Count());
    }

    /// <summary>ブラシを配色の見分けが付く文字列にする。<b>色を読めるのは凍結済みのブラシだけ</b>
    /// ——Loomo 側プリセット（<c>EditorThemePresets</c>）は凍結して配るが、ライブラリ内蔵の配色
    /// （Dracula/Dark/Nord/…）は凍結されておらず<b>最初に触れたスレッドに所有される</b>。並列実行では
    /// 別スレッドのテストが先に作ることがあり、そのまま <c>ToString()</c> すると
    /// <c>VerifyAccess</c> で落ちる（実際にこのテストが不定期に落ちていた）。所有権を跨げない
    /// ブラシは同一性（参照）で見分ける——このテストが見たいのは「別々の配色へ解決されるか」なので、
    /// 別インスタンスであることが分かれば足りる。</summary>
    private static string Describe(Brush brush)
    {
        // Freezable の IsFrozen/Color は、所有 Dispatcher と別スレッドから読むと
        // VerifyAccess で例外になる。テーマの識別には参照 identity だけで十分なので、
        // WPF プロパティへ触れずに比較する。
        return $"#{RuntimeHelpers.GetHashCode(brush):X8}";
    }
}
