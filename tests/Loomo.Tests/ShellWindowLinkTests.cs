using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class ShellWindowLinkTests
{
    [Theory]
    [InlineData("C:\\Projects\\Loomo\\src\\Foo.cs:10:5")]
    [InlineData("C:/Projects/Loomo/src/Foo.cs:10")]
    public void Windowsの行番号付きパスはfile_URI扱いでもTerminalのパスとして残す(string target)
    {
        Assert.True(Uri.TryCreate(target, UriKind.Absolute, out var uri));
        Assert.True(uri.IsFile);
        Assert.True(TerminalLinkTargetResolver.IsWindowsPathTarget(target));
    }

    [Theory]
    [InlineData("mailto:user@example.com")]
    [InlineData("https://example.com")]
    public void Windows絶対パス以外はパス扱いにしない(string target)
        => Assert.False(TerminalLinkTargetResolver.IsWindowsPathTarget(target));

    [Fact]
    public void 環境変数で始まるパスは展開してWindowsパスにする()
    {
        var expanded = TerminalLinkTargetResolver.ExpandShellPath("%APPDATA%/Loomo/editor-positions.json:3");

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/Loomo/editor-positions.json:3",
            expanded);
        Assert.True(TerminalLinkTargetResolver.IsWindowsPathTarget(expanded));
    }

    [Fact]
    public void チルダで始まるパスはユーザーフォルダーへ展開する()
        => Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\.claude\\settings.json",
            TerminalLinkTargetResolver.ExpandShellPath("~\\.claude\\settings.json"));

    [Theory]
    [InlineData("src/Foo.cs:12")]
    [InlineData("%LOOMO_UNDEFINED_VAR_FOR_TEST%/a.txt")]
    [InlineData("~user/a.txt")]
    public void 展開できないものはそのまま残す(string target)
        => Assert.Equal(target, TerminalLinkTargetResolver.ExpandShellPath(target));
}
