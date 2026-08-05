using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Tests;

public class EditorFileLinkResolverTests
{
    [Fact]
    public void TryResolve_PercentEncodedMarkdownPath()
    {
        using var temp = new TempWorkspace();
        var docs = Directory.CreateDirectory(Path.Combine(temp.Root, "docs")).FullName;
        var images = Directory.CreateDirectory(Path.Combine(temp.Root, "images")).FullName;
        var document = Path.Combine(docs, "guide.md");
        var target = Path.Combine(images, "new image.png");
        File.WriteAllText(document, "");
        File.WriteAllText(target, "");

        var ok = FileLinkResolver.TryResolve(
            "../images/new%20image.png", document, temp.Root,
            out var fullPath, out _, out _, out _);

        Assert.True(ok);
        Assert.Equal(target, fullPath);
    }

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
            baseFolder: null,
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

    // ── マルチルート：基準は「その文書を担当するフォルダー」 ──────────────────────
    //
    // ワークスペースを受ける overload は基準を currentDocumentPath から導くので、
    // 呼ぶ側が基準を取り違えられない。ここでは同じ相対パス src/Program.cs を
    // プライマリと追加フォルダーの両方に置いて、**どちらを掴むか**で判定する
    // （プライマリ固定に戻ると、存在はするので解決自体は成功し、別のファイルが開く）。

    [Fact]
    public void ワークスペース版_追加フォルダーの文書はそのフォルダーを基準にする()
    {
        using var temp = new TempWorkspace();
        var (workspace, primary, added) = TwoFolderWorkspace(temp);
        var document = temp.Write("added", "docs", "README.md", "# docs");
        var decoy = temp.Write("primary", "src", "Program.cs", "// primary");
        var expected = temp.Write("added", "src", "Program.cs", "// added");

        var ok = FileLinkResolver.TryResolve(
            workspace, Path.Combine("src", "Program.cs"), document,
            out var fullPath, out _, out _, out _);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(expected), fullPath);
        Assert.NotEqual(Path.GetFullPath(decoy), fullPath);
    }

    [Fact]
    public void ワークスペース版_プライマリの文書はプライマリを基準にする()
    {
        using var temp = new TempWorkspace();
        var (workspace, _, _) = TwoFolderWorkspace(temp);
        var document = temp.Write("primary", "docs", "README.md", "# docs");
        var expected = temp.Write("primary", "src", "Program.cs", "// primary");
        temp.Write("added", "src", "Program.cs", "// added");

        var ok = FileLinkResolver.TryResolve(
            workspace, Path.Combine("src", "Program.cs"), document,
            out var fullPath, out _, out _, out _);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(expected), fullPath);
    }

    /// <summary>ワークスペース外の文書は基準が決まらないのでプライマリへ倒す
    /// （解決を諦めるより、プライマリ基準で開ける方が使える）。</summary>
    [Fact]
    public void ワークスペース版_ワークスペース外の文書はプライマリへ倒す()
    {
        using var temp = new TempWorkspace();
        var (workspace, _, _) = TwoFolderWorkspace(temp);
        var outside = temp.Write("outside", "note.md", "# note");
        var expected = temp.Write("primary", "src", "Program.cs", "// primary");

        var ok = FileLinkResolver.TryResolve(
            workspace, Path.Combine("src", "Program.cs"), outside,
            out var fullPath, out _, out _, out _);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(expected), fullPath);
    }

    [Fact]
    public void ワークスペース版_リンク種別の振り分けも同じ基準を使う()
    {
        using var temp = new TempWorkspace();
        var (workspace, _, _) = TwoFolderWorkspace(temp);
        var document = temp.Write("added", "docs", "README.md", "# docs");
        temp.Write("primary", "src", "Program.cs", "// primary");
        var expected = temp.Write("added", "src", "Program.cs", "// added");

        var target = LinkOpenTargetResolver.Resolve(
            workspace, Path.Combine("src", "Program.cs"), document);

        Assert.Equal(LinkOpenTargetKind.File, target.Kind);
        Assert.Equal(Path.GetFullPath(expected), target.Value);
    }

    /// <summary>プライマリ＋追加フォルダーの 2 フォルダーワークスペース。</summary>
    private static (FakeWorkspaceService Workspace, string Primary, string Added) TwoFolderWorkspace(
        TempWorkspace temp)
    {
        var primary = Directory.CreateDirectory(Path.Combine(temp.Root, "primary")).FullName;
        var added = Directory.CreateDirectory(Path.Combine(temp.Root, "added")).FullName;
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(primary);
        workspace.AddFolder(added);
        return (workspace, primary, added);
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
