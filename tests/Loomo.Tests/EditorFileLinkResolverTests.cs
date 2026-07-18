using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Tests;

public class EditorFileLinkResolverTests
{
    [Fact]
    public void RelativePath_ResolvesAgainstCurrentDocumentDirectory()
    {
        using var temp = new TempWorkspace();
        var current = temp.Write("docs", "README.md", "# docs");
        var target = temp.Write("docs", "guide.md", "# guide");

        var ok = FileLinkResolver.TryResolve(
            "guide.md",
            current,
            temp.Root,
            out var fullPath,
            out var line,
            out var column,
            out var isDirectory);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(target), fullPath);
        Assert.Equal(0, line);
        Assert.Equal(0, column);
        Assert.False(isDirectory);
    }

    [Fact]
    public void RelativePath_FallsBackToWorkspaceRoot()
    {
        using var temp = new TempWorkspace();
        var current = temp.Write("docs", "README.md", "# docs");
        var target = temp.Write("src", "Program.cs", "class Program {}");

        var ok = FileLinkResolver.TryResolve(
            Path.Combine("src", "Program.cs"),
            current,
            temp.Root,
            out var fullPath,
            out _,
            out _,
            out var isDirectory);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(target), fullPath);
        Assert.False(isDirectory);
    }

    [Fact]
    public void TrailingLineColumn_IsParsedForFiles()
    {
        using var temp = new TempWorkspace();
        var target = temp.Write("src", "Program.cs", "class Program {}");

        var ok = FileLinkResolver.TryResolve(
            target + ":12:4",
            currentDocumentPath: null,
            workspaceRoot: null,
            out var fullPath,
            out var line,
            out var column,
            out var isDirectory);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(target), fullPath);
        Assert.Equal(12, line);
        Assert.Equal(4, column);
        Assert.False(isDirectory);
    }

    [Fact]
    public void DirectoryPath_IsRecognized()
    {
        using var temp = new TempWorkspace();
        var dir = Directory.CreateDirectory(Path.Combine(temp.Root, "docs")).FullName;

        var ok = FileLinkResolver.TryResolve(
            "docs",
            currentDocumentPath: null,
            temp.Root,
            out var fullPath,
            out _,
            out _,
            out var isDirectory);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(dir), fullPath);
        Assert.True(isDirectory);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "loomo-editor-link-" + Guid.NewGuid().ToString("N"));

        public string Write(params string[] parts)
        {
            if (parts.Length < 2)
                throw new ArgumentException("Specify at least a file name and content.", nameof(parts));

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
