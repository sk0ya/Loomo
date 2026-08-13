using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ファイル一覧（エクスプローラ）ペインの検証。ツリーが不得手な仕事——並べ替え・絞り込み・
/// 1フォルダーの平らな一覧——が正しく出ること、現在地がワークスペース配下から出ないこと、
/// 復元（§24.4）で現在地と並びが戻ることを見る。表示の見た目は対象外。
/// </summary>
public sealed class FilesPaneTests : IDisposable
{
    private readonly string _root;
    private readonly string _sub;
    private readonly FakeWorkspaceService _workspace = new();

    public FilesPaneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"loomo-files-{Guid.NewGuid():N}");
        _sub = Path.Combine(_root, "src");
        Directory.CreateDirectory(_sub);
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        WriteFile(Path.Combine(_root, "file2.txt"), "22", new DateTime(2026, 1, 2));
        WriteFile(Path.Combine(_root, "file10.txt"), "1", new DateTime(2026, 3, 4));
        WriteFile(Path.Combine(_root, "app.cs"), new string('x', 4096), new DateTime(2025, 12, 1));
        WriteFile(Path.Combine(_sub, "inner.cs"), "", new DateTime(2026, 2, 1));
        _workspace.OpenFolder(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private static void WriteFile(string path, string content, DateTime modified)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, modified);
    }

    private FilesPaneViewModel CreateSut()
    {
        var sut = new FilesPaneViewModel(_workspace, new FolderTreeCommandHandler(_workspace));
        sut.Restore(snapshot: null, fallbackFolder: _root);
        return sut;
    }

    [Fact]
    public void 初期表示はプライマリフォルダーでフォルダーが先頭かつ名前は自然順()
    {
        var sut = CreateSut();

        Assert.Equal(_root, sut.CurrentFolder);
        Assert.Equal(
            new[] { "docs", "src", "app.cs", "file2.txt", "file10.txt" },
            sut.Entries.Select(e => e.Name));
        Assert.Equal("2 フォルダー・3 ファイル", sut.StatusText);
    }

    [Fact]
    public void 更新日時で並べ替えると新しい順から始まる()
    {
        var sut = CreateSut();

        sut.SortCommand.Execute("Modified");

        Assert.Equal(FilesSortColumn.Modified, sut.SortColumn);
        Assert.True(sut.SortDescending);   // 日時は「新しい方を見たい」ので降順から
        Assert.Equal(" ▼", sut.ModifiedSortMark);
        // フォルダーは並べ替え列に関わらず常に先頭（グループ内は同じ列で並ぶ）。
        Assert.Equal(2, sut.Entries.TakeWhile(e => e.IsDirectory).Count());
        Assert.Equal(
            new[] { "file10.txt", "file2.txt", "app.cs" },
            sut.Entries.Where(e => !e.IsDirectory).Select(e => e.Name));

        sut.SortCommand.Execute("Modified");   // 同じ列をもう一度 → 向きが反転
        Assert.False(sut.SortDescending);
        Assert.Equal("app.cs", sut.Entries.First(e => !e.IsDirectory).Name);   // 最も古いものが先頭
    }

    [Fact]
    public void サイズで並べ替えると大きい順から始まる()
    {
        var sut = CreateSut();

        sut.SortCommand.Execute("Size");

        Assert.Equal("app.cs", sut.Entries.First(e => !e.IsDirectory).Name);
    }

    [Fact]
    public void 絞り込みは部分一致とワイルドカードに効く()
    {
        var sut = CreateSut();

        sut.Filter = "file";
        Assert.Equal(new[] { "file2.txt", "file10.txt" }, sut.Entries.Select(e => e.Name));
        Assert.Contains("3 件を非表示", sut.StatusText);

        sut.Filter = "*.cs";
        Assert.Equal(new[] { "app.cs" }, sut.Entries.Select(e => e.Name));

        sut.Filter = "";
        Assert.Equal(5, sut.Entries.Count);
    }

    [Fact]
    public void 隠しファイルは既定で伏せられる()
    {
        var hidden = Path.Combine(_root, "secret.txt");
        File.WriteAllText(hidden, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var sut = CreateSut();
        Assert.DoesNotContain(sut.Entries, e => e.Name == "secret.txt");

        sut.ShowHiddenFiles = true;
        Assert.Contains(sut.Entries, e => e.Name == "secret.txt");
    }

    [Fact]
    public void 移動は戻る進む上へを覚える()
    {
        var sut = CreateSut();
        Assert.False(sut.CanGoBack);
        Assert.False(sut.CanGoUp);   // ワークスペースフォルダー自身が上限

        sut.Navigate(_sub);
        Assert.Equal(_sub, sut.CurrentFolder);
        Assert.True(sut.CanGoBack);
        Assert.True(sut.CanGoUp);

        sut.GoBackCommand.Execute(null);
        Assert.Equal(_root, sut.CurrentFolder);
        Assert.True(sut.CanGoForward);

        sut.GoForwardCommand.Execute(null);
        Assert.Equal(_sub, sut.CurrentFolder);

        sut.GoUpCommand.Execute(null);
        Assert.Equal(_root, sut.CurrentFolder);
    }

    [Fact]
    public void ワークスペース外へは移動しない()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"loomo-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var sut = CreateSut();
            sut.Navigate(outside);
            Assert.Equal(_root, sut.CurrentFolder);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* 無視 */ }
        }
    }

    [Fact]
    public void パンくずはワークスペースフォルダーから現在地までを並べる()
    {
        var sut = CreateSut();
        sut.Navigate(_sub);

        Assert.Equal(new[] { Path.GetFileName(_root), "src" }, sut.Breadcrumbs.Select(b => b.Name));
        Assert.True(sut.Breadcrumbs[^1].IsLast);
    }

    [Fact]
    public void 現在地と並びは保存して戻せる()
    {
        var sut = CreateSut();
        sut.Navigate(_sub);
        sut.SortCommand.Execute("Size");
        sut.ShowHiddenFiles = true;
        var snapshot = sut.Capture();

        var restored = new FilesPaneViewModel(_workspace, new FolderTreeCommandHandler(_workspace));
        restored.Restore(snapshot, _root);

        Assert.Equal(_sub, restored.CurrentFolder);
        Assert.Equal(FilesSortColumn.Size, restored.SortColumn);
        Assert.True(restored.ShowHiddenFiles);
        Assert.False(restored.CanGoBack);   // 履歴までは持ち越さない
    }

    [Fact]
    public void 保存されたフォルダーが消えていればプライマリへ倒す()
    {
        var sut = CreateSut();
        sut.Restore(
            new FilesPaneSnapshot { CurrentFolder = Path.Combine(_root, "消えたフォルダー") }, _root);

        Assert.Equal(_root, sut.CurrentFolder);
    }

    [Fact]
    public void 更新しても同じ行インスタンスが残る()
    {
        var sut = CreateSut();
        var before = sut.Entries.Single(e => e.Name == "file2.txt");

        File.WriteAllText(Path.Combine(_root, "file2.txt"), "22222");
        sut.RefreshCommand.Execute(null);

        var after = sut.Entries.Single(e => e.Name == "file2.txt");
        Assert.Same(before, after);           // 作り直すと選択とスクロール位置が飛ぶ
        Assert.Equal(5, after.Size);          // 値だけ更新される
    }

    [Fact]
    public void 新規作成は現在地に作られ名前の変更はタブ追従のために通知される()
    {
        var sut = CreateSut();
        sut.Navigate(_sub);

        var created = sut.CreateEntry("memo.md", isDirectory: false);
        Assert.Equal(Path.Combine(_sub, "memo.md"), created);
        Assert.Contains(sut.Entries, e => e.Name == "memo.md");
        Assert.Equal(created, sut.PendingSelection);

        EntryRenamedEventArgs? renamed = null;
        sut.EntryRenamed += (_, e) => renamed = e;
        var entry = sut.Entries.Single(e => e.Name == "memo.md");
        var newPath = sut.RenameEntry(entry, "note.md");

        Assert.Equal(Path.Combine(_sub, "note.md"), newPath);
        Assert.NotNull(renamed);
        Assert.Equal(created, renamed!.Value.OldPath);
        Assert.Equal(newPath, renamed.Value.NewPath);
        Assert.Contains(sut.Entries, e => e.Name == "note.md");
    }

    [Fact]
    public void 貼り付けは同名を一意化して現在地へ複製する()
    {
        var sut = CreateSut();
        sut.Navigate(_sub);

        var first = sut.PasteEntry(Path.Combine(_root, "app.cs"), move: false);
        var second = sut.PasteEntry(Path.Combine(_root, "app.cs"), move: false);

        Assert.Equal(Path.Combine(_sub, "app.cs"), first);
        Assert.NotEqual(first, second);                       // 上書きしない
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(Path.Combine(_root, "app.cs")));   // コピー元は残る
    }

    [Fact]
    public void 相対パスは所属するワークスペースフォルダー基準になる()
    {
        var sut = CreateSut();
        sut.Navigate(_sub);
        var inner = sut.Entries.Single(e => e.Name == "inner.cs");
        Assert.Equal(Path.Combine("src", "inner.cs"), sut.RelativePathFor(inner));

        var added = Path.Combine(Path.GetTempPath(), $"loomo-files2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(added);
        try
        {
            _workspace.AddFolder(added);
            File.WriteAllText(Path.Combine(added, "other.txt"), "");
            sut.Navigate(added);
            var other = sut.Entries.Single(e => e.Name == "other.txt");

            // プライマリ基準にすると「..\..\」だらけになる。所属フォルダー基準であること。
            Assert.Equal("other.txt", sut.RelativePathFor(other));
        }
        finally
        {
            try { Directory.Delete(added, recursive: true); } catch { /* 無視 */ }
        }
    }
}
