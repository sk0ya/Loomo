using System.Linq;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>Diff ペインの変更ファイル一覧をフォルダ階層に組む（Git パネルの変更ツリーと同じ見え方）。</summary>
public class DiffFileTreeTests
{
    private static DiffFileItem File(string path)
        => new() { FullPath = @"C:\repo\" + path.Replace('/', '\\'), DisplayPath = path, Badge = "M" };

    [Fact]
    public void フォルダ優先で並び単一子のフォルダはまとめられる()
    {
        var roots = DiffFileTreeNode.Build(new[]
        {
            File("README.md"),
            File("src/Loomo.App/Views/A.xaml"),
            File("src/Loomo.App/ViewModels/B.cs"),
        });

        Assert.Equal(new[] { "src/Loomo.App", "README.md" }, roots.Select(n => n.Name));
        var app = roots[0];
        Assert.True(app.IsDirectory);
        Assert.Equal(2, app.LeafCount);
        Assert.Equal(new[] { "ViewModels", "Views" }, app.Children.Select(n => n.Name));
        Assert.Equal("src/Loomo.App/Views", app.Children[1].Key);
    }

    [Fact]
    public void ファイル行は画面に並ぶ順で列挙される()
    {
        var roots = DiffFileTreeNode.Build(new[]
        {
            File("z.txt"),
            File("a/b.cs"),
            File("a/a.cs"),
        });

        Assert.Equal(new[] { "a/a.cs", "a/b.cs", "z.txt" },
            DiffFileTreeNode.Leaves(roots).Select(n => n.File!.DisplayPath));
    }

    [Fact]
    public void 比較項目は階層に割らず元の順で先頭に並ぶ()
    {
        var newer = new DiffFileItem
        {
            FullPath = "", DisplayPath = "a/b ↔ クリップボード", Badge = "≠",
            Comparison = new DiffComparison("a/b", "1", "クリップボード", "2"),
        };
        var older = new DiffFileItem
        {
            FullPath = "", DisplayPath = "選択 ↔ クリップボード", Badge = "≠",
            Comparison = new DiffComparison("選択", "1", "クリップボード", "2"),
        };

        var roots = DiffFileTreeNode.Build(new[] { newer, older });

        Assert.Equal(new[] { newer, older }, roots.Select(n => n.File));
        Assert.All(roots, n => Assert.Empty(n.Children));
    }
}
