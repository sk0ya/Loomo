using System.IO;
using Editor.Core.Buffer;
using Editor.Core.Models;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>取り消しの履歴の置き場（§24.96）。</summary>
public sealed class UndoHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-undo-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 書いた履歴を同じパスで読め_大文字小文字の違うパスでも同じ置き場になる()
    {
        var store = new UndoHistoryStore(_root, synchronous: true);
        store.Save(@"C:\Work\App\Program.cs", "{\"v\":1}");

        Assert.Equal("{\"v\":1}", store.Load(@"c:\work\app\program.cs"));
        Assert.Null(store.Load(@"C:\Work\App\Other.cs"));
        Assert.Equal(UndoHistoryStore.DocumentId(@"C:\Work\App\Program.cs"), UndoHistoryStore.DocumentId(@"c:\work\APP\program.cs"));
    }

    [Fact]
    public void 古いものは起動のついでに刈る()
    {
        Directory.CreateDirectory(_root);
        var old = Path.Combine(_root, "old.json");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - UndoHistoryStore.Retention - TimeSpan.FromDays(1));
        var fresh = Path.Combine(_root, "fresh.json");
        File.WriteAllText(fresh, "{}");

        _ = new UndoHistoryStore(_root, synchronous: true);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void エディタが書き出した履歴を置いて読み戻すと_同じ本文なら取り消せ_外で書き換わった本文には付かない()
    {
        // エディタが閉じる前に書き出し（ExportUndoHistory と同じ呼び方）、開き直したときに戻す（ImportUndoHistory と同じ）。
        var path = @"C:\Work\App\Note.txt";
        var id = UndoHistoryStore.DocumentId(path);
        var buffer = new TextBuffer("one");
        var undo = new UndoManager();
        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        var store = new UndoHistoryStore(_root, synchronous: true);
        store.Save(path, undo.ExportHistory(buffer, id, new UndoExportOptions(MaxCharacters: UndoHistoryStore.MaxCharacters))!);

        var reopened = new TextBuffer("one two");
        var restored = new UndoManager();
        Assert.True(restored.ImportHistory(store.Load(path)!, reopened, id).Success);
        restored.Undo(reopened, CursorPosition.Zero);
        Assert.Equal("one", reopened.GetText());

        var changedOutside = new TextBuffer("one two three");
        Assert.False(new UndoManager().ImportHistory(store.Load(path)!, changedOutside, id).Success);
    }
}
