using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class FileDragDropTests
{
    [Fact]
    public void ExistingPaths_normalizes_deduplicates_and_drops_missing_paths()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "a.txt");
        File.WriteAllText(file, "a");

        var paths = FileDragDrop.ExistingPaths(new[] { file, Path.Combine(temp.Path, "missing"), file + "\\" });

        Assert.Single(paths);
        Assert.Equal(Path.GetFullPath(file), paths[0]);
    }

    [Fact]
    public void PowerShellQuote_escapes_quotes_and_preserves_special_path_characters()
    {
        var path = @"C:\work\O'Brien & [draft]\file.txt";

        Assert.Equal("'C:\\work\\O''Brien & [draft]\\file.txt'", FileDragDrop.PowerShellQuote(path));
    }

    [Fact]
    public void TryGetPaths_handles_file_drop_data_and_deduplicates_it()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "日本語 & O'Brien.txt");
        File.WriteAllText(file, "content");
        var data = new System.Windows.DataObject();
        FileDragDrop.SetPaths(data, new[] { file, file });

        var paths = FileDragDrop.TryGetPaths(data);

        Assert.Single(paths);
        Assert.Equal(Path.GetFullPath(file), paths[0]);
    }

    [Fact]
    public void TryGetPaths_treats_unreadable_or_non_file_data_as_an_invalid_drop()
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.FileDrop, new object[] { 42 });

        Assert.Empty(FileDragDrop.TryGetPaths(data));
        Assert.Empty(FileDragDrop.TryGetPaths(null));
    }

    [Fact]
    public void CommonDirectory_uses_the_deepest_common_parent_for_files_and_folders()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        var nested = Directory.CreateDirectory(Path.Combine(source.FullName, "nested"));
        var first = Path.Combine(source.FullName, "one.cs");
        var second = Path.Combine(nested.FullName, "two.cs");
        File.WriteAllText(first, "1");
        File.WriteAllText(second, "2");

        Assert.Equal(Path.GetFullPath(source.FullName), FileDragDrop.CommonDirectory(new[] { first, second }));
        Assert.Equal(Path.GetFullPath(nested.FullName), FileDragDrop.CommonDirectory(new[] { nested.FullName, second }));
    }

    [Fact]
    public void CommonDirectory_returns_null_when_all_items_are_invalid()
    {
        Assert.Null(FileDragDrop.CommonDirectory(new[] { "Z:\\does-not-exist\\file.txt" }));
    }

    [Fact]
    public void CommonDirectory_returns_the_directory_for_a_single_directory_drop()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder"));

        Assert.Equal(Path.GetFullPath(folder.FullName), FileDragDrop.CommonDirectory(new[] { folder.FullName }));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Loomo.FileDragDropTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
