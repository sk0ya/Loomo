using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>選んでいる文字列から、ファイル検索・テキスト検索の最初の検索語を作る（§24.2.3）。</summary>
public class PaletteSelectionSeedTests
{
    [Theory]
    [InlineData("ShellWindow.cs", "ShellWindow.cs")]
    [InlineData("  src/Loomo.App/App.xaml.cs  ", "src/Loomo.App/App.xaml.cs")]
    [InlineData(@"src\Loomo.App\App.xaml.cs", "src/Loomo.App/App.xaml.cs")]
    [InlineData("src/foo.ts:12:5", "src/foo.ts")]
    [InlineData("src/foo.ts:12", "src/foo.ts")]
    [InlineData(@"C:\Projects\Loomo\src\Foo.cs(12,5)", "Foo.cs")]
    [InlineData("/home/me/app/main.py", "main.py")]
    [InlineData("\"./docs/設計/README.md\"", "docs/設計/README.md")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("./.editorconfig", ".editorconfig")]
    [InlineData("\"src/foo.ts\":12", "src/foo.ts")]
    [InlineData("'src/foo.ts:12:5'", "src/foo.ts")]
    public void File_mode_shapes_paths_into_a_file_query(string selection, string expected)
        => Assert.Equal(expected, PaletteSelectionSeed.For(PaletteMode.File, selection));

    [Fact]
    public void Text_mode_keeps_the_selection_verbatim_but_trimmed()
        => Assert.Equal("Foo(bar):12", PaletteSelectionSeed.For(PaletteMode.Text, "  Foo(bar):12 "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    public void Unusable_selections_give_no_seed(string? selection)
    {
        Assert.Null(PaletteSelectionSeed.For(PaletteMode.File, selection));
        Assert.Null(PaletteSelectionSeed.For(PaletteMode.Text, selection));
    }

    [Fact]
    public void Too_long_selection_is_not_a_query()
        => Assert.Null(PaletteSelectionSeed.For(PaletteMode.Text, new string('a', PaletteSelectionSeed.MaxChars + 1)));

    [Theory]
    [InlineData(PaletteMode.All)]
    [InlineData(PaletteMode.Command)]
    [InlineData(PaletteMode.Symbol)]
    public void Only_file_and_text_searches_take_a_seed(PaletteMode mode)
        => Assert.Null(PaletteSelectionSeed.For(mode, "Foo"));
}
