using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>閉じたファイルのカーソル位置の置き場。</summary>
public sealed class EditorPositionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-editor-position-tests", Guid.NewGuid().ToString("N"));

    private string StoreFile => Path.Combine(_root, "editor-positions.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private EditorPositionStore Open(Func<string, bool>? exists = null)
        => new(StoreFile, synchronous: true, fileExists: exists ?? (_ => true));

    [Fact]
    public async Task 覚えた位置は次に開いたストアでも引け_大文字小文字の違うパスでも同じ()
    {
        Open().Remember(@"C:\Work\App\Program.cs", new EditorPosition(120, 8, 0.4));

        var position = await Open().GetAsync(@"c:\work\app\program.cs");

        Assert.Equal(new EditorPosition(120, 8, 0.4), position);
        Assert.Null(await Open().GetAsync(@"C:\Work\App\Other.cs"));
    }

    [Fact]
    public async Task 先頭へ戻したファイルは忘れる()
    {
        var store = Open();
        store.Remember(@"C:\Work\a.cs", new EditorPosition(10, 0, 0.1));
        store.Remember(@"C:\Work\a.cs", new EditorPosition(0, 0, 0));

        Assert.Null(await Open().GetAsync(@"C:\Work\a.cs"));
    }

    [Fact]
    public async Task 長く使っていないものと_もう無いファイルは起動のついでに刈る()
    {
        var store = Open();
        store.Remember(@"C:\Work\old.cs", new EditorPosition(5, 0, null), DateTime.UtcNow - EditorPositionStore.Retention - TimeSpan.FromDays(1));
        store.Remember(@"C:\Work\gone.cs", new EditorPosition(5, 0, null));
        store.Remember(@"C:\Work\fresh.cs", new EditorPosition(5, 0, null));

        var reopened = Open(exists: path => !path.EndsWith("GONE.CS", StringComparison.Ordinal));

        Assert.Null(await reopened.GetAsync(@"C:\Work\old.cs"));
        Assert.Null(await reopened.GetAsync(@"C:\Work\gone.cs"));
        Assert.NotNull(await reopened.GetAsync(@"C:\Work\fresh.cs"));
        Assert.DoesNotContain("GONE.CS", File.ReadAllText(StoreFile));   // 刈った結果は書き戻す
    }

    [Fact]
    public async Task 上限を超えたら_いちばん長く使っていないものから落とす()
    {
        var store = Open();
        var start = DateTime.UtcNow - TimeSpan.FromDays(1);
        for (var i = 0; i <= EditorPositionStore.MaxEntries; i++)
            store.Remember($@"C:\Work\f{i}.cs", new EditorPosition(1, 0, null), start + TimeSpan.FromSeconds(i));

        Assert.Null(await store.GetAsync(@"C:\Work\f0.cs"));
        Assert.NotNull(await store.GetAsync(@"C:\Work\f1.cs"));
        Assert.NotNull(await store.GetAsync($@"C:\Work\f{EditorPositionStore.MaxEntries}.cs"));
    }

    [Fact]
    public async Task 名前を変えたら新しい名前へ付け替える()
    {
        var store = Open();
        store.Remember(@"C:\Work\a.cs", new EditorPosition(30, 2, null));
        store.Move(@"C:\Work\a.cs", @"C:\Work\b.cs", isDirectory: false);

        Assert.Null(await store.GetAsync(@"C:\Work\a.cs"));
        Assert.Equal(new EditorPosition(30, 2, null), await store.GetAsync(@"C:\Work\b.cs"));
    }

    [Fact]
    public async Task 壊れたファイルは覚えていないのと同じ扱い()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StoreFile, "{ not json");

        var store = Open();
        Assert.Null(await store.GetAsync(@"C:\Work\a.cs"));
        store.Remember(@"C:\Work\a.cs", new EditorPosition(3, 0, null));
        Assert.NotNull(await Open().GetAsync(@"C:\Work\a.cs"));
    }

    [Fact]
    public async Task フォルダー名を変えたら_その下のファイルを全部付け替え_名前の似た隣のフォルダーは触らない()
    {
        var store = Open();
        store.Remember(@"C:\Work\Old\a.cs", new EditorPosition(1, 0, null));
        store.Remember(@"C:\Work\Old\Sub\b.cs", new EditorPosition(2, 0, null));
        store.Remember(@"C:\Work\Older\c.cs", new EditorPosition(3, 0, null));
        store.Move(@"C:\Work\Old", @"C:\Work\New", isDirectory: true);

        Assert.Equal(1, (await store.GetAsync(@"C:\Work\New\a.cs"))?.Line);
        Assert.Equal(2, (await store.GetAsync(@"C:\Work\New\Sub\b.cs"))?.Line);
        Assert.Null(await store.GetAsync(@"C:\Work\Old\a.cs"));
        Assert.Equal(3, (await store.GetAsync(@"C:\Work\Older\c.cs"))?.Line);
    }

    [Fact]
    public async Task 読み込みが済む前の変更は来た順に当たり_閉じるときに書き切る()
    {
        var store = new EditorPositionStore(StoreFile, fileExists: _ => true);   // 背景で読み込む本番の形
        store.Remember(@"C:\Work\a.cs", new EditorPosition(10, 0, null));
        store.Remember(@"C:\Work\a.cs", new EditorPosition(200, 0, null));
        store.Dispose();

        Assert.Equal(200, (await Open().GetAsync(@"C:\Work\a.cs"))?.Line);
    }

    [Fact]
    public async Task まとめて覚えても1件ずつと同じ()
    {
        Open().RememberMany([(@"C:\Work\a.cs", new EditorPosition(4, 1, null)), (@"C:\Work\b.cs", new EditorPosition(0, 0, null))]);

        var reopened = Open();
        Assert.Equal(new EditorPosition(4, 1, null), await reopened.GetAsync(@"C:\Work\a.cs"));
        Assert.Null(await reopened.GetAsync(@"C:\Work\b.cs"));
    }

    [Fact]
    public async Task 二つのLoomoが同時に開いていても_互いに覚えた分を消さない()
    {
        var first = Open();
        var second = Open();   // first が書く前に読み込んだ
        first.Remember(@"C:\Work\a.cs", new EditorPosition(10, 0, null));
        second.Remember(@"C:\Work\b.cs", new EditorPosition(20, 0, null));

        var reopened = Open();
        Assert.Equal(10, (await reopened.GetAsync(@"C:\Work\a.cs"))?.Line);
        Assert.Equal(20, (await reopened.GetAsync(@"C:\Work\b.cs"))?.Line);
    }

    [Fact]
    public async Task 自分が忘れたファイルを_ディスクに残っていた古い記録で蘇らせない()
    {
        Open().Remember(@"C:\Work\a.cs", new EditorPosition(10, 0, null));
        var store = Open();
        store.Remember(@"C:\Work\a.cs", new EditorPosition(0, 0, null));   // 先頭へ戻した＝忘れる

        Assert.Null(await Open().GetAsync(@"C:\Work\a.cs"));
    }
}
