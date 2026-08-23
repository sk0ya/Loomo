using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class FilePropertiesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-properties-{Guid.NewGuid():N}");

    public FilePropertiesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ファイルのサイズ日時属性場所権限を読み取れる()
    {
        var path = Path.Combine(_root, "readme.txt");
        File.WriteAllText(path, "hello");

        var item = new FilePropertiesService()
            .ReadMany([new FilePropertiesTarget(path, IsDirectory: false)])
            .Items.Single();

        Assert.Null(item.Error);
        Assert.Equal("readme.txt", item.Name);
        Assert.False(item.IsDirectory);
        Assert.Equal(5, item.SizeBytes);
        Assert.False(item.IsSizeIncomplete);
        Assert.NotNull(item.CreationTime);
        Assert.NotNull(item.LastWriteTime);
        Assert.NotNull(item.Attributes);
        Assert.Equal(_root, item.Location);
        // ACL の取得自体が成功したことを確認する。規則数やアカウント名は実行環境に依存する。
        Assert.NotEmpty(item.Permissions);
        Assert.Null(item.PermissionError);
    }

    [Fact]
    public void フォルダーは配下ファイルのサイズと属性を表示できる()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        File.WriteAllBytes(Path.Combine(directory, "a.bin"), new byte[7]);
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllBytes(Path.Combine(directory, "nested", "b.bin"), new byte[11]);

        var item = new FilePropertiesService()
            .ReadMany([new FilePropertiesTarget(directory, IsDirectory: true)])
            .Items.Single();

        Assert.Null(item.Error);
        Assert.True(item.IsDirectory);
        Assert.Equal(18, item.SizeBytes);
        Assert.False(item.IsSizeIncomplete);
        Assert.Equal("src", item.Name);
        Assert.Equal(_root, item.Location);
    }

    [Fact]
    public void 複数選択は順序を保ち非存在項目だけをエラー表示する()
    {
        var first = Path.Combine(_root, "first.txt");
        File.WriteAllText(first, "1");
        var missing = Path.Combine(_root, "gone.txt");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        var result = new FilePropertiesService().ReadMany([
            new FilePropertiesTarget(first, false),
            new FilePropertiesTarget(missing, false),
            new FilePropertiesTarget(folder, true),
        ]);

        Assert.Equal(3, result.Count);
        Assert.Equal("first.txt", result.Items[0].Name);
        Assert.Contains("見つかりません", result.Items[1].Error);
        Assert.Null(result.Items[2].Error);
        Assert.Equal("3 個の項目を選択中", result.SelectionDisplay);
    }

    [Fact]
    public void アクセス拒否は他の項目を巻き込まずエラー項目になる()
    {
        var denied = new FilePropertiesTarget(Path.Combine(_root, "secret.txt"), false);
        var service = new FilePropertiesService(_ => throw new UnauthorizedAccessException());

        var item = service.ReadMany([denied]).Items.Single();

        Assert.Contains("アクセスが拒否", item.Error);
        Assert.Equal("secret.txt", item.Name);
    }

    [Fact]
    public void UNCと長いパスの接頭辞を正規化で壊さない()
    {
        const string unc = @"\\server\share\folder\item.txt";
        const string extendedUnc = @"\\?\UNC\server\share\folder\item.txt";
        const string extendedLocal = @"\\?\C:\very-long-folder\item.txt";

        Assert.Equal(Path.GetFullPath(unc), FilePropertiesService.NormalizePath(unc));
        Assert.Equal(extendedUnc, FilePropertiesService.NormalizePath(extendedUnc));
        Assert.Equal(extendedLocal, FilePropertiesService.NormalizePath(extendedLocal));
    }

    [Fact]
    public void キャンセル済みの読み取りは開始前にキャンセルされる()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new FilePropertiesService().ReadMany(
                [new FilePropertiesTarget(Path.Combine(_root, "file.txt"), false)],
                cancellation.Token));
    }

}
