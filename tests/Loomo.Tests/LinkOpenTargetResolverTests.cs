using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Tests;

/// <summary>右クリック「別ウィンドウで開く」の宛先振り分け（URL＝ブラウザ／ファイル＝エディタ）。</summary>
public class LinkOpenTargetResolverTests
{
    [Fact]
    public void HttpUrl_IsBrowserTarget()
    {
        var target = LinkOpenTargetResolver.Resolve("https://example.com/a?b=1", null, null);

        Assert.Equal(LinkOpenTargetKind.Url, target.Kind);
        Assert.Equal("https://example.com/a?b=1", target.Value);
    }

    [Fact]
    public void RelativePath_ResolvesAgainstDocumentAndKeepsLineColumn()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        var file = temp.Write("src", "Program.cs", "class Program {}");

        var target = LinkOpenTargetResolver.Resolve("../src/Program.cs:12:4", document, temp.Root);

        Assert.Equal(LinkOpenTargetKind.File, target.Kind);
        Assert.Equal(Path.GetFullPath(file), target.Value);
        Assert.Equal(12, target.Line);
        Assert.Equal(4, target.Column);
    }

    [Fact]
    public void FileUri_IsResolvedAsFile()
    {
        using var temp = new TempWorkspace();
        var file = temp.Write("docs", "guide.md", "# guide");

        var target = LinkOpenTargetResolver.Resolve(new Uri(file).AbsoluteUri, null, temp.Root);

        Assert.Equal(LinkOpenTargetKind.File, target.Kind);
        Assert.Equal(Path.GetFullPath(file), target.Value);
    }

    [Fact]
    public void Directory_IsNotAWindowTarget()
    {
        using var temp = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(temp.Root, "docs"));

        var target = LinkOpenTargetResolver.Resolve("docs", null, temp.Root);

        Assert.Equal(LinkOpenTargetKind.Directory, target.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("does-not-exist.md")]
    public void UnopenableTargets_AreNone(string href)
    {
        using var temp = new TempWorkspace();
        Directory.CreateDirectory(temp.Root);

        var target = LinkOpenTargetResolver.Resolve(href, null, temp.Root);

        Assert.Equal(LinkOpenTargetKind.None, target.Kind);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "loomo-link-window-" + Guid.NewGuid().ToString("N"));

        public string Write(params string[] parts)
        {
            var content = parts[^1];
            var path = Path.Combine(new[] { Root }.Concat(parts[..^1]).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
