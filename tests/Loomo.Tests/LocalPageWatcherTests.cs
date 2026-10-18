using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ブラウザのローカルのページを保存で読み直す見張り（§24.45）。見張るのはページが読んだファイルだけで、
/// 同じフォルダーの別ファイルでは読み直さない。フォルダーは非再帰・上限つき。</summary>
public sealed class LocalPageWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-localpage-" + Guid.NewGuid().ToString("N"));

    public LocalPageWatcherTests() => Directory.CreateDirectory(Path.Combine(_root, "css"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string File(string relative)
    {
        var path = Path.Combine(_root, relative);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    private static bool Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [Fact]
    public void 読んだファイルが変わったら知らせる()
    {
        var page = File("index.html");
        var css = File(@"css\site.css");
        var count = 0;
        using var watcher = new LocalPageWatcher(() => Interlocked.Increment(ref count), action => action());
        watcher.Watch([page, css]);

        watcher.HandleChange(css);
        watcher.HandleChange(css);   // 1 回の保存が複数のイベントに割れても 1 回

        Assert.True(Wait(() => Volatile.Read(ref count) > 0));
        Thread.Sleep(400);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void 読んでいないファイルでは知らせない()
    {
        var page = File("index.html");
        var other = File("notes.md");
        var count = 0;
        using var watcher = new LocalPageWatcher(() => Interlocked.Increment(ref count), action => action());
        watcher.Watch([page]);

        watcher.HandleChange(other);

        Thread.Sleep(400);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void 止めたら知らせない()
    {
        var page = File("index.html");
        var count = 0;
        using var watcher = new LocalPageWatcher(() => Interlocked.Increment(ref count), action => action());
        watcher.Watch([page]);
        watcher.Stop();

        watcher.HandleChange(page);

        Thread.Sleep(400);
        Assert.Equal(0, Volatile.Read(ref count));
        Assert.Empty(watcher.Files);
    }

    [Fact]
    public void フォルダーは重複を除き上限まで()
    {
        var files = Enumerable.Range(0, LocalPageWatcher.MaxFolders + 3).Select(i =>
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "d" + i)).FullName;
            return Path.Combine(dir, "a.js");
        }).Append(Path.Combine(_root, "d0", "b.js")).ToList();

        var folders = LocalPageWatcher.FoldersOf(files);

        Assert.Equal(LocalPageWatcher.MaxFolders, folders.Count);
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
