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
}
