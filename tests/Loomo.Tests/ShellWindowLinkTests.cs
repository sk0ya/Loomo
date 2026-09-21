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
}
