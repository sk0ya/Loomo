using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public sealed class NewFileDialogTests
{
    [Theory]
    [InlineData("MainWindow", ".cs", "MainWindow.cs")]
    [InlineData("MainWindow", "xaml", "MainWindow.xaml")]
    [InlineData("notes", ".pochi.json", "notes.pochi.json")]
    [InlineData("README.md", ".cs", "README.md")]
    [InlineData(".gitignore", ".txt", ".gitignore")]
    [InlineData("notes", "（なし）", "notes")]
    public void ファイル名と拡張子を結合する(string name, string extension, string expected)
    {
        Assert.Equal(expected, NewFileDialog.ComposeFileName(name, extension));
    }
}
