using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>エクスプローラの選択を見ているファイルへ合わせる先。開いている枝の中だけを辿り、畳まれたフォルダーは開かない。</summary>
public sealed class FolderTreeActiveFileFollowTests
{
    private sealed class Node(string path, bool isDirectory, bool expanded = false, params Node[] children)
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public bool Expanded { get; } = expanded;
        public Node[] Children { get; } = children;
    }

    private static Node? Follow(IEnumerable<Node> roots, string? path)
        => FolderTreeActiveFileFollow.VisibleRowFor(roots, path,
            n => n.Path, n => n.IsDirectory, n => n.Expanded, n => n.Children);

    [Fact]
    public void FileInsideAnOpenFolderIsSelected()
    {
        var program = new Node(@"C:\w\src\Program.cs", false);
        var src = new Node(@"C:\w\src", true, expanded: true, program, new Node(@"C:\w\src\Util.cs", false));
        Assert.Same(program, Follow([src, new Node(@"C:\w\README.md", false)], @"C:\w\src\Program.cs"));
    }

    [Fact]
    public void FileInsideAFoldedFolderIsNotRevealed()
    {
        var src = new Node(@"C:\w\src", true, expanded: false, new Node(@"C:\w\src\Program.cs", false));
        Assert.Null(Follow([src], @"C:\w\src\Program.cs"));
    }

    [Fact]
    public void FoldedAnywhereOnTheWayStops()
    {
        var deep = new Node(@"C:\w\a\b", true, expanded: false, new Node(@"C:\w\a\b\x.cs", false));
        var a = new Node(@"C:\w\a", true, expanded: true, deep);
        Assert.Null(Follow([a], @"C:\w\a\b\x.cs"));
    }

    [Fact]
    public void FileOutsideTheTreeOrNoFileClearsTheSelection()
    {
        var roots = new[] { new Node(@"C:\w\README.md", false) };
        Assert.Null(Follow(roots, @"D:\other\note.md"));
        Assert.Null(Follow(roots, null));
    }

    [Fact]
    public void NestedChildUnderAnExpandedParentFileIsSelected()
    {
        var designer = new Node(@"C:\w\Form1.Designer.cs", false);
        var form = new Node(@"C:\w\Form1.cs", false, expanded: true, designer);
        Assert.Same(designer, Follow([form], @"C:\w\Form1.Designer.cs"));
    }

    [Fact]
    public void NestedChildUnderACollapsedParentFileIsNotRevealed()
    {
        var form = new Node(@"C:\w\Form1.cs", false, expanded: false, new Node(@"C:\w\Form1.Designer.cs", false));
        Assert.Null(Follow([form], @"C:\w\Form1.Designer.cs"));
    }
}
