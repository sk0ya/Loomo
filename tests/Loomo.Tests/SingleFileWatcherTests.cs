using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 1ファイル監視（§24.8）のデバウンス・張り替え・破棄。<see cref="FileSystemWatcher"/> の実発火は
/// タイミング依存で不安定なので、<b>時計（デバウンス幅）と UI スレッドへ渡す手段を注入</b>し、
/// 監視のハンドラ本体（<c>HandleChange</c>）を直接叩いて確かめる。
/// </summary>
public class SingleFileWatcherTests
{
    private static readonly TimeSpan ShortDebounce = TimeSpan.FromMilliseconds(30);

    /// <summary>デバウンス満了後の通知を待つ（待ち切れなければ false）。</summary>
    private static bool Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [Fact]
    public void 保存で割れた複数のイベントは1回に畳まれる()
    {
        var seen = new List<string>();
        using var watcher = new SingleFileWatcher(
            path => { lock (seen) seen.Add(path); }, ShortDebounce, post: action => action());

        // エディタの1回の保存は「切り詰め→書き込み→属性更新」のように複数イベントへ割れる。
        watcher.HandleChange(@"C:\work\a.html");
        watcher.HandleChange(@"C:\work\a.html");
        watcher.HandleChange(@"C:\work\a.html");

        Assert.True(Wait(() => { lock (seen) return seen.Count > 0; }));
        Thread.Sleep(ShortDebounce + ShortDebounce);   // 遅れて追加の通知が来ないことまで見る
        lock (seen)
            Assert.Equal(new[] { @"C:\work\a.html" }, seen);
    }

    [Fact]
    public void 破棄したあとは通知しない()
    {
        var count = 0;
        var watcher = new SingleFileWatcher(_ => Interlocked.Increment(ref count), ShortDebounce, action => action());

        watcher.HandleChange(@"C:\work\a.html");
        watcher.Dispose();   // デバウンス満了前に破棄＝保留中の1件も捨てる

        Thread.Sleep(200);
        Assert.Equal(0, Volatile.Read(ref count));

        watcher.HandleChange(@"C:\work\a.html");   // 破棄後の発火も無視する
        Thread.Sleep(200);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void 監視先を張り替えると前のファイルは見なくなる()
    {
        var folder = Directory.CreateTempSubdirectory("loomo-watch-").FullName;
        try
        {
            var a = Path.Combine(folder, "a.html");
            var b = Path.Combine(folder, "b.html");
            File.WriteAllText(a, "<p>a</p>");
            File.WriteAllText(b, "<p>b</p>");

            using var watcher = new SingleFileWatcher(_ => { }, ShortDebounce, action => action());

            watcher.Watch(a);
            Assert.Equal(a, watcher.Target);

            watcher.Watch(b);              // 張り替え：前のファイルは残らない
            Assert.Equal(b, watcher.Target);

            watcher.Watch(null);           // 見張る理由が無くなったら止める（張りっぱなしにしない）
            Assert.Null(watcher.Target);

            watcher.Watch(b);
            watcher.Dispose();
            Assert.Null(watcher.Target);   // 破棄でも必ず外れる
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void 見張れない対象では監視を持たない()
    {
        using var watcher = new SingleFileWatcher(_ => { }, ShortDebounce, action => action());

        watcher.Watch(@"C:\loomo-does-not-exist-9d3f\a.html");   // 親フォルダーが無い
        Assert.Null(watcher.Target);

        watcher.Watch("   ");
        Assert.Null(watcher.Target);
    }

    [Fact]
    public void 同じ対象への張り替えは監視を作り直さない()
    {
        var folder = Directory.CreateTempSubdirectory("loomo-watch-").FullName;
        try
        {
            var a = Path.Combine(folder, "a.html");
            File.WriteAllText(a, "<p>a</p>");
            using var watcher = new SingleFileWatcher(_ => { }, ShortDebounce, action => action());

            watcher.Watch(a);
            Assert.Equal(1, watcher.WatchGeneration);

            // 描画のたびに呼ばれる口なので、綴りが違っても同じファイルなら張り替えない
            // （作り直すと、その入れ替わりの瞬間の変更を取りこぼす）。
            watcher.Watch(Path.Combine(folder, ".", "a.html"));
            watcher.Watch(a.ToUpperInvariant());
            Assert.Equal(1, watcher.WatchGeneration);
            Assert.Equal(a, watcher.Target);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
