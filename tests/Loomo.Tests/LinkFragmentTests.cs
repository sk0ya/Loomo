using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Tests;

/// <summary>リンクの <c>#…</c> を行へ読み替える（`other.md#見出し`・`Foo.cs#L12`）。</summary>
public class LinkFragmentTests
{
    private const string Guide = "# ガイド\n\n```\n# コードの中は見出しではない\n```\n\n## Getting Started\n\n本文\n\n## インストール (Windows)\n";

    [Fact]
    public void RelativeMarkdownLink_WithPreviewHeadingId_LandsOnHeadingLine()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        var guide = temp.Write("docs", "guide.md", Guide);

        // プレビューの見出しの「#」でコピーされる形（小文字・空白は -）。
        var target = LinkOpenTargetResolver.Resolve("guide.md#getting-started", document, temp.Root);

        Assert.Equal(LinkOpenTargetKind.File, target.Kind);
        Assert.Equal(Path.GetFullPath(guide), target.Value);
        Assert.Equal(7, target.Line);
    }

    [Fact]
    public void GitHubSlug_DropsPunctuation_AndKeepsJapanese()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        temp.Write("docs", "guide.md", Guide);

        var target = LinkOpenTargetResolver.Resolve("guide.md#インストール-windows", document, temp.Root);

        Assert.Equal(11, target.Line);
    }

    [Fact]
    public void PercentEncodedFragment_IsDecoded()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        temp.Write("docs", "guide.md", Guide);

        var target = LinkOpenTargetResolver.Resolve("guide.md#" + Uri.EscapeDataString("ガイド"), document, temp.Root);

        Assert.Equal(1, target.Line);
    }

    [Fact]
    public void HashInsideCodeFence_IsNotAHeading()
    {
        var lines = Guide.Split('\n');

        Assert.Equal(0, LinkFragment.HeadingLineOf(lines, "コードの中は見出しではない"));
    }

    [Fact]
    public void UnknownHeading_StillOpensFile()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        var guide = temp.Write("docs", "guide.md", Guide);

        var target = LinkOpenTargetResolver.Resolve("guide.md#no-such-heading", document, temp.Root);

        Assert.Equal(LinkOpenTargetKind.File, target.Kind);
        Assert.Equal(Path.GetFullPath(guide), target.Value);
        Assert.Equal(0, target.Line);
    }

    [Fact]
    public void GitHubLineAnchor_LandsOnLine_ForAnyFile()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        temp.Write("src", "Program.cs", "class Program {}");

        Assert.Equal(12, LinkOpenTargetResolver.Resolve("../src/Program.cs#L12", document, temp.Root).Line);
        Assert.Equal(12, LinkOpenTargetResolver.Resolve("../src/Program.cs#L12-L20", document, temp.Root).Line);
    }

    [Fact]
    public void FileNameContainingHash_IsResolvedWhole()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        var csharp = temp.Write("docs", "C#.md", "# C#\n\n## Records\n");

        Assert.Equal(Path.GetFullPath(csharp), LinkOpenTargetResolver.Resolve("C#.md", document, temp.Root).Value);
        var withHeading = LinkOpenTargetResolver.Resolve("C#.md#records", document, temp.Root);
        Assert.Equal(Path.GetFullPath(csharp), withHeading.Value);
        Assert.Equal(3, withHeading.Line);
    }

    [Fact]
    public void FileUri_KeepsFragment()
    {
        using var temp = new TempWorkspace();
        var guide = temp.Write("docs", "guide.md", Guide);

        var target = LinkOpenTargetResolver.Resolve(new Uri(guide).AbsoluteUri + "#getting-started", null, temp.Root);

        Assert.Equal(Path.GetFullPath(guide), target.Value);
        Assert.Equal(7, target.Line);
    }

    [Fact]
    public void ExplicitLineSuffix_WinsOverFragment()
    {
        using var temp = new TempWorkspace();
        var document = temp.Write("docs", "README.md", "# docs");
        temp.Write("docs", "guide.md", Guide);

        // パス:行 があればそちら（# は名前の一部として扱われず、行は :3 のまま）。
        Assert.Equal(3, LinkOpenTargetResolver.Resolve("./guide.md:3", document, temp.Root).Line);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "loomo-link-fragment-" + Guid.NewGuid().ToString("N"));

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
