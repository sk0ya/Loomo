using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 開いているタブの外部変更（§24.57）：自分の保存を外部変更と取り違えない判定と、タブのファイルを
/// 見張る監視の張り替え。
/// </summary>
public class ExternalFileChangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loomo-extchange-" + Guid.NewGuid().ToString("N"));

    public ExternalFileChangeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void 覚えた指紋と同じなら_未保存の編集があっても何もしない()
    {
        // 保存の直後に打ち始めたタブ：自分の保存で監視が発火しても競合にしてはいけない。
        Assert.Equal(ExternalFileChangeAction.None, ExternalFileChangePolicy.Decide(isModified: true, "A", "A"));
        Assert.Equal(ExternalFileChangeAction.None, ExternalFileChangePolicy.Decide(isModified: false, "A", "A"));
    }

    [Fact]
    public void 指紋が変わったら_未保存なら競合_そうでなければ読み直し()
    {
        Assert.Equal(ExternalFileChangeAction.Conflict, ExternalFileChangePolicy.Decide(isModified: true, "A", "B"));
        Assert.Equal(ExternalFileChangeAction.Reload, ExternalFileChangePolicy.Decide(isModified: false, "A", "B"));
    }

    [Fact]
    public void 読めないときは次の通知を待つ()
    {
        // 書き込み途中・削除：消えたファイルのタブを空にしたり競合にしたりしない。
        Assert.Equal(ExternalFileChangeAction.None, ExternalFileChangePolicy.Decide(isModified: true, "A", null));
        Assert.Equal(ExternalFileChangeAction.None, ExternalFileChangePolicy.Decide(isModified: false, "A", null));
    }

    [Fact]
    public void 基準が分からなければ変わったものとして扱う()
    {
        // 未保存なら人に選ばせ、保存済みなら本文比較つきの読み直しに回す（同じ本文なら読み直さない）。
        Assert.Equal(ExternalFileChangeAction.Conflict, ExternalFileChangePolicy.Decide(isModified: true, null, "B"));
        Assert.Equal(ExternalFileChangeAction.Reload, ExternalFileChangePolicy.Decide(isModified: false, null, "B"));
    }

    [Fact]
    public void 指紋はバイト列で取る_Shift_JISでも同じ中身なら同じ()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding(932).GetBytes("日本語\r\n");
        var a = WriteFile("a.txt", sjis);
        var before = ExternalFileChangePolicy.Fingerprint(a);
        File.WriteAllBytes(a, sjis);
        Assert.NotNull(before);
        Assert.Equal(before, ExternalFileChangePolicy.Fingerprint(a));

        File.WriteAllBytes(a, Encoding.GetEncoding(932).GetBytes("日本語!\r\n"));
        Assert.NotEqual(before, ExternalFileChangePolicy.Fingerprint(a));
    }

    [Fact]
    public void 無いファイルの指紋はnull()
        => Assert.Null(ExternalFileChangePolicy.Fingerprint(Path.Combine(_dir, "missing.txt")));

    [Fact]
    public void 見張りは集合に合わせて差分だけ張り替える()
    {
        var created = new List<SingleFileWatcher>();
        using var set = new EditorFileWatchSet(_ => { }, callback => {
            var w = new SingleFileWatcher(callback, post: action => action());
            created.Add(w);
            return w;
        });
        var a = WriteFile("a.cs", [1]);
        var b = WriteFile("b.cs", [2]);

        // 同じファイルを2枚のタブで開いていても監視は1つ（表記の揺れも同じファイル）。
        set.Sync([a, a.ToUpperInvariant(), b, null, ""]);
        Assert.Equal(2, set.Targets.Count);
        Assert.Equal(2, created.Count);

        // 残ったファイルの監視は作り直さない（作り直す瞬間の変更を取りこぼすため）。
        set.Sync([a]);
        Assert.Single(set.Targets);
        Assert.Equal(2, created.Count);
        Assert.Null(created[1].Target);   // b の監視は止まった
        Assert.Equal(Path.GetFullPath(a), created[0].Target, ignoreCase: true);

        set.Sync([]);
        Assert.Empty(set.Targets);
        Assert.Null(created[0].Target);
    }
}
