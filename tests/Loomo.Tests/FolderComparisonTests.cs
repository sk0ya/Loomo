using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>フォルダー同士の比較（§24.5.3）：違うファイルだけを 1 件ずつ比較にする。</summary>
public sealed class FolderComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-foldercmp-{Guid.NewGuid():N}");
    private string Left => Path.Combine(_root, "v1");
    private string Right => Path.Combine(_root, "v2");

    public FolderComparisonTests()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Lists_changed_added_and_removed_files_and_counts_identical_ones()
    {
        Write(Left, "same.txt", "a");
        Write(Right, "same.txt", "a");
        Write(Left, "src/changed.cs", "old");
        Write(Right, "src/changed.cs", "new");
        Write(Left, "gone.md", "bye");
        Write(Right, "docs/new.md", "hi");

        var result = FolderComparison.Build(Left, Right);

        Assert.Equal(1, result.Identical);
        Assert.Equal(["docs/new.md", "gone.md", "src/changed.cs"], result.Comparisons.Select(c => c.DisplayPath));
        var added = result.Comparisons[0];
        Assert.Equal("追加", added.Badge);
        Assert.Equal("", added.LeftText);
        Assert.Equal(Path.Combine(Right, "docs", "new.md"), added.FilePath);
        var removed = result.Comparisons[1];
        Assert.Equal("削除", removed.Badge);
        Assert.True(removed.FileIsLeft);
        var changed = result.Comparisons[2];
        Assert.Equal(("old", "new"), (changed.LeftText, changed.RightText));
        Assert.Equal("v1/src/changed.cs", changed.LeftTitle);
        Assert.Equal("", changed.Badge);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Line_ending_only_differences_count_as_identical()
    {
        Write(Left, "a.txt", "one\r\ntwo\r\n");
        Write(Right, "a.txt", "one\ntwo\n");

        var result = FolderComparison.Build(Left, Right);

        Assert.Empty(result.Comparisons);
        Assert.Equal(1, result.Identical);
    }

    [Fact]
    public void Line_counts_are_computed_up_front()
    {
        Write(Left, "a.txt", "x\n");
        Write(Right, "a.txt", "x\ny\nz\n");

        var c = Assert.Single(FolderComparison.Build(Left, Right).Comparisons);

        Assert.Equal((2, 0), c.Stat);
        Assert.Equal((0, 2), c.Swapped().Stat);
    }

    [Fact]
    public void Too_many_files_on_one_side_compares_nothing()
    {
        // 片側を途中まで数えて比べると、数え残しが「片側にしか無い」に化ける。だから一部だけ見せない。
        for (var i = 0; i < 3; i++) Write(Left, $"f{i}.txt", "x");
        Write(Right, "f0.txt", "y");

        var result = FolderComparison.Build(Left, Right, maxFilesPerSide: 2);

        Assert.True(result.TooManyFiles);
        Assert.Empty(result.Comparisons);
        Assert.Contains("多すぎる", FolderComparison.Summarize(result));
    }

    [Fact]
    public void Binary_differences_are_reported_but_not_listed()
    {
        File.WriteAllBytes(Path.Combine(Left, "image.png"), [0x89, 0x50, 0x00, 0x01]);
        File.WriteAllBytes(Path.Combine(Right, "image.png"), [0x89, 0x50, 0x00, 0x02]);

        var result = FolderComparison.Build(Left, Right);

        Assert.Empty(result.Comparisons);
        Assert.Equal(["image.png"], result.Skipped);
    }

    [Fact]
    public void Git_directory_is_not_compared()
    {
        Write(Left, ".git/HEAD", "ref: refs/heads/main");
        Write(Right, ".git/HEAD", "ref: refs/heads/feature");

        var result = FolderComparison.Build(Left, Right);

        Assert.Empty(result.Comparisons);
        Assert.Equal(0, result.Identical);
    }

    [Fact]
    public void Same_named_folders_are_told_apart_by_full_path()
    {
        var left = Path.Combine(_root, "a", "app");
        var right = Path.Combine(_root, "b", "app");
        Write(left, "x.txt", "1");
        Write(right, "x.txt", "2");

        var c = Assert.Single(FolderComparison.Build(left, right).Comparisons);

        Assert.StartsWith(left, c.LeftTitle);
        Assert.StartsWith(right, c.RightTitle);
    }

    [Fact]
    public void Swapping_a_folder_entry_flips_added_and_removed()
    {
        Write(Right, "new.txt", "x");
        var added = Assert.Single(FolderComparison.Build(Left, Right).Comparisons);

        var swapped = added.Swapped();

        Assert.Equal("削除", swapped.Badge);
        Assert.Equal("new.txt", swapped.DisplayPath);
    }
}
