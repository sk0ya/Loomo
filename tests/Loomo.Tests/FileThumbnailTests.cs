using System.IO;
using System.Collections.Concurrent;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

[Collection(WindowsShellTests.Name)]
public sealed class FileThumbnailTests
{
    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("manual.pdf", true)]
    [InlineData("source.cs", false)]
    [InlineData("archive.zip", false)]
    public void サムネイル対象形式を絞り込む(string name, bool expected)
        => Assert.Equal(expected, ThumbnailSupport.IsSupported(name));

    [Theory]
    [InlineData(FilesDisplayMode.Details, 0)]
    [InlineData(FilesDisplayMode.List, 0)]
    [InlineData(FilesDisplayMode.LargeIcons, 128)]
    [InlineData(FilesDisplayMode.MediumIcons, 96)]
    [InlineData(FilesDisplayMode.SmallIcons, 64)]
    [InlineData(FilesDisplayMode.Tiles, 96)]
    public void 表示モードごとの要求サイズを固定する(FilesDisplayMode mode, int expected)
        => Assert.Equal(expected, ThumbnailSupport.EdgeFor(mode));

    [Fact]
    public async Task キャッシュは表示サイズとファイル変更をキーにし256件を上限にする()
    {
        using var folder = new TemporaryFolder();
        var calls = 0;
        var service = new FileThumbnailService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return FileIcons.ImageFor(FileIconData.DefaultFileIndex);
        });

        var first = Path.Combine(folder.Path, "first.png");
        File.WriteAllText(first, "first");
        await service.GetThumbnailAsync(first, 96);
        await service.GetThumbnailAsync(first, 96);
        Assert.Equal(1, calls);

        // 表示サイズが違えば別のキャッシュエントリになる。
        await service.GetThumbnailAsync(first, 128);
        Assert.Equal(2, calls);

        // 同じパスでも内容が変わったら古い画像を返さない。
        File.WriteAllText(first, "changed-content");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(2));
        await service.GetThumbnailAsync(first, 96);
        Assert.Equal(3, calls);

        // 256件を超えた最古の項目はLRUから追い出される。
        for (var i = 0; i < 256; i++)
        {
            var path = Path.Combine(folder.Path, $"item-{i}.png");
            File.WriteAllText(path, i.ToString());
            await service.GetThumbnailAsync(path, 96);
        }

        await service.GetThumbnailAsync(first, 96);
        Assert.Equal(260, calls); // 3 + 256 + 追い出された first
    }

    [Fact]
    public async Task Shell取得は同時に最大3件でキャンセル待機はスロットを増やさない()
    {
        using var folder = new TemporaryFolder();
        var firstRelease = new ManualResetEventSlim(false);
        var secondRelease = new ManualResetEventSlim(false);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0;
        var active = 0;
        var firstActive = 0;
        var secondActive = 0;
        var secondMax = 0;

        var service = new FileThumbnailService((_, _) =>
        {
            var current = Interlocked.Increment(ref active);
            if (Volatile.Read(ref phase) == 0)
            {
                var entered = Interlocked.Increment(ref firstActive);
                if (entered == 3) firstEntered.TrySetResult(true);
                firstRelease.Wait();
            }
            else
            {
                var entered = Interlocked.Increment(ref secondActive);
                UpdateMaximum(ref secondMax, entered);
                if (entered == 3) secondEntered.TrySetResult(true);
                secondRelease.Wait();
            }

            Interlocked.Decrement(ref active);
            return FileIcons.ImageFor(FileIconData.DefaultFileIndex);
        });

        var paths = Enumerable.Range(0, 10).Select(i =>
        {
            var path = Path.Combine(folder.Path, $"item-{i}.png");
            File.WriteAllText(path, i.ToString());
            return path;
        }).ToArray();

        var firstBatch = paths.Take(3).Select(path => service.GetThumbnailAsync(path, 96)).ToArray();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(await service.GetThumbnailAsync(paths[3], 96, cancelled.Token));

        firstRelease.Set();
        await Task.WhenAll(firstBatch);

        Volatile.Write(ref phase, 1);
        var secondBatch = paths.Skip(4).Take(6).Select(path => service.GetThumbnailAsync(path, 96)).ToArray();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        Assert.Equal(3, secondMax);

        secondRelease.Set();
        await Task.WhenAll(secondBatch);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task WindowsShellは実在する画像からサムネイルを返す()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "photo.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        var image = await new FileThumbnailService().GetThumbnailAsync(path, 96);

        Assert.NotNull(image);
    }

    [Fact]
    public async Task 非対応形式と壊れた画像はShell取得を通常アイコンへフォールバックする()
    {
        using var folder = new TemporaryFolder();
        var text = Path.Combine(folder.Path, "source.cs");
        var broken = Path.Combine(folder.Path, "broken.png");
        File.WriteAllText(text, "class C {}");
        File.WriteAllText(broken, "not an image");
        var service = new FileThumbnailService();

        Assert.Null(await service.GetThumbnailAsync(text, 96));
        Assert.Null(await service.GetThumbnailAsync(broken, 96));
        Assert.NotNull(new FileEntryViewModel(broken, isDirectory: false, size: 12, modified: DateTime.UtcNow).IconImage);
    }

    [Fact]
    public async Task アイコン系表示では対応形式だけを非同期取得する()
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(Path.Combine(folder.Path, "photo.jpg"), "not a real image");
        File.WriteAllText(Path.Combine(folder.Path, "clip.mp4"), "video");
        File.WriteAllText(Path.Combine(folder.Path, "manual.pdf"), "pdf");
        File.WriteAllText(Path.Combine(folder.Path, "source.cs"), "class C {}");

        var thumbnails = new RecordingThumbnailService();
        using var column = CreateColumn(folder.Path, thumbnails);
        column.DisplayMode = FilesDisplayMode.MediumIcons;

        await thumbnails.WaitForCallsAsync(3);

        Assert.Equal(
            new[] { "clip.mp4", "manual.pdf", "photo.jpg" },
            thumbnails.Calls.Select(call => Path.GetFileName(call.Path)).OrderBy(name => name));
        Assert.DoesNotContain(thumbnails.Calls, call => call.Path.EndsWith("source.cs", StringComparison.OrdinalIgnoreCase));
        Assert.All(thumbnails.Calls, call => Assert.Equal(96, call.Edge));
    }

    [Fact]
    public async Task 表示モード変更で保留中の取得をキャンセルし通常アイコンへ戻る()
    {
        using var folder = new TemporaryFolder();
        var imagePath = Path.Combine(folder.Path, "photo.png");
        File.WriteAllText(imagePath, "not a real image");
        var thumbnails = new BlockingThumbnailService();
        using var column = CreateColumn(folder.Path, thumbnails);

        column.DisplayMode = FilesDisplayMode.LargeIcons;
        await thumbnails.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var entry = column.Entries.Single(item => item.FullPath == imagePath);

        column.DisplayMode = FilesDisplayMode.Details;

        Assert.True(thumbnails.Cancellation.IsCancellationRequested);
        Assert.Null(entry.ThumbnailImage);
        Assert.NotNull(entry.IconImage);
    }

    [Fact]
    public async Task フォルダー移動後に古い一覧の結果を適用しない()
    {
        using var folder = new TemporaryFolder();
        var oldPath = Path.Combine(folder.Path, "old.png");
        File.WriteAllText(oldPath, "not a real image");
        var nextPath = Path.Combine(folder.Path, "next");
        Directory.CreateDirectory(nextPath);
        var thumbnails = new NonCancellableThumbnailService();
        using var column = CreateColumn(folder.Path, thumbnails);
        column.DisplayMode = FilesDisplayMode.LargeIcons;
        await thumbnails.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        column.Navigate(nextPath);
        thumbnails.Release.SetResult(FileIcons.ImageFor(FileIconData.DefaultFileIndex));
        await Task.Delay(50);

        Assert.DoesNotContain(column.Entries, entry => entry.FullPath == oldPath);
        Assert.DoesNotContain(column.Entries, entry => entry.ThumbnailImage is not null);
    }

    private sealed class RecordingThumbnailService : IFileThumbnailService
    {
        private readonly ConcurrentQueue<(string Path, int Edge)> _calls = new();
        private readonly TaskCompletionSource<bool> _firstCalls = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string Path, int Edge)> Calls => _calls.ToArray();

        public Task<ImageSource?> GetThumbnailAsync(string path, int edge, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue((path, edge));
            if (_calls.Count >= 3)
                _firstCalls.TrySetResult(true);
            return Task.FromResult<ImageSource?>(null);
        }

        public Task WaitForCallsAsync(int count) => _firstCalls.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static FilesColumnViewModel CreateColumn(string path, IFileThumbnailService thumbnails)
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(path);
        var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-thumb-workflows-{Guid.NewGuid():N}")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
        tree.LoadRoot(path);
        var column = new FilesColumnViewModel(
            workspace, FolderTreeCommandHandler.Unconfined(workspace, new FileOperationHistory()),
            tree, new FakeFilePlacesProvider(), thumbnails: thumbnails);
        column.Restore(snapshot: null, fallbackFolder: path);
        return column;
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class BlockingThumbnailService : IFileThumbnailService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImageSource?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Cancellation { get; private set; }

        public async Task<ImageSource?> GetThumbnailAsync(string path, int edge, CancellationToken cancellationToken = default)
        {
            Cancellation = cancellationToken;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return FileIcons.ImageFor(FileIconData.DefaultFileIndex);
        }
    }

    private sealed class NonCancellableThumbnailService : IFileThumbnailService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImageSource?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ImageSource?> GetThumbnailAsync(string path, int edge, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            // 実際のShell呼び出しがキャンセルに追従できず遅れて返る場合を再現する。
            await Release.Task;
            return FileIcons.ImageFor(FileIconData.DefaultFileIndex);
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"loomo-thumb-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
