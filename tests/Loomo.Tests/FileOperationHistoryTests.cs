using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>エクスプローラー（フォルダーツリー／ファイル一覧ペイン）のファイル操作の Undo／Redo。
/// 記録は <see cref="FolderTreeCommandHandler"/>、逆操作は <see cref="FileOperationHistory"/>。
///
/// <para>削除の Undo だけは Windows の実ゴミ箱を経由する（<see cref="RecycleBin"/> が
/// <c>$Recycle.Bin</c> のメタデータから元の場所へ戻す）ため、この 1 本だけは環境依存。
/// ゴミ箱を無効化した環境では失敗する＝そこは実装ではなく前提が違う、と分かるようにしておく。</para></summary>
[Collection(WindowsShellTests.Name)]
public sealed class FileOperationHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-fileops-{Guid.NewGuid():N}");
    private readonly FileOperationHistory _history = new();
    private readonly FolderTreeCommandHandler _commands;

    public FileOperationHistoryTests()
    {
        Directory.CreateDirectory(_root);
        _commands = FolderTreeCommandHandler.Unconfined(new FakeWorkspaceService(), _history);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private string Path2(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void 作成を元に戻すと消え_やり直すと戻る()
    {
        var created = _commands.Create(_root, "new.txt", isDirectory: false);
        Assert.True(File.Exists(created));

        _history.Undo();
        Assert.False(File.Exists(created));

        _history.Redo();
        Assert.True(File.Exists(created));
    }

    [Fact]
    public void 名前の変更を元に戻すと元の名前に戻る()
    {
        File.WriteAllText(Path2("a.txt"), "中身");
        var renamed = _commands.Rename(Path2("a.txt"), "b.txt", isDirectory: false);
        Assert.Equal(Path2("b.txt"), renamed);

        var result = _history.Undo();
        Assert.True(File.Exists(Path2("a.txt")));
        Assert.False(File.Exists(Path2("b.txt")));
        Assert.Equal("中身", File.ReadAllText(Path2("a.txt")));
        Assert.Equal("名前の変更「b.txt」", result.Description);

        _history.Redo();
        Assert.True(File.Exists(Path2("b.txt")));
    }

    [Fact]
    public void 移動を元に戻すと元のフォルダーへ戻る()
    {
        Directory.CreateDirectory(Path2("sub"));
        File.WriteAllText(Path2("a.txt"), "x");

        var moved = _commands.Paste(Path2("sub"), Path2("a.txt"), move: true);
        Assert.Equal(Path2("sub", "a.txt"), moved);

        _history.Undo();
        Assert.True(File.Exists(Path2("a.txt")));
        Assert.False(File.Exists(Path2("sub", "a.txt")));

        _history.Redo();
        Assert.True(File.Exists(Path2("sub", "a.txt")));
    }

    [Fact]
    public void コピーを元に戻すと複製だけが消える()
    {
        File.WriteAllText(Path2("a.txt"), "x");
        var copied = _commands.Paste(_root, Path2("a.txt"), move: false);
        Assert.Equal(Path2("a - コピー.txt"), copied);   // 同名衝突は拡張子の手前に「 - コピー」を入れて一意化される

        _history.Undo();
        Assert.False(File.Exists(copied));
        Assert.True(File.Exists(Path2("a.txt")));       // 元は触らない

        _history.Redo();
        Assert.True(File.Exists(copied));
    }

    [Fact]
    public void フォルダーの移動も中身ごと戻る()
    {
        Directory.CreateDirectory(Path2("src", "inner"));
        File.WriteAllText(Path2("src", "inner", "deep.txt"), "深い");
        Directory.CreateDirectory(Path2("dst"));

        _commands.Paste(Path2("dst"), Path2("src"), move: true);
        Assert.True(File.Exists(Path2("dst", "src", "inner", "deep.txt")));

        _history.Undo();
        Assert.True(File.Exists(Path2("src", "inner", "deep.txt")));
        Assert.False(Directory.Exists(Path2("dst", "src")));
    }

    [Fact]
    public void 一括操作は一回のUndoでまとめて戻る()
    {
        File.WriteAllText(Path2("a.txt"), "1");
        File.WriteAllText(Path2("b.txt"), "2");
        Directory.CreateDirectory(Path2("sub"));

        using (_history.BeginBatch())
        {
            _commands.Paste(Path2("sub"), Path2("a.txt"), move: true);
            _commands.Paste(Path2("sub"), Path2("b.txt"), move: true);
        }

        Assert.Equal("移動 2件", _history.UndoDescription);
        _history.Undo();

        Assert.True(File.Exists(Path2("a.txt")));
        Assert.True(File.Exists(Path2("b.txt")));
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void Undoのあと新しい操作をするとRedoは消える()
    {
        _commands.Create(_root, "a.txt", isDirectory: false);
        _history.Undo();
        Assert.True(_history.CanRedo);

        _commands.Create(_root, "b.txt", isDirectory: false);
        Assert.False(_history.CanRedo);
        Assert.Equal("作成「b.txt」", _history.UndoDescription);
    }

    [Fact]
    public void 行き先が塞がっていると戻さずに理由を返す()
    {
        File.WriteAllText(Path2("a.txt"), "元");
        _commands.Rename(Path2("a.txt"), "b.txt", isDirectory: false);
        File.WriteAllText(Path2("a.txt"), "あとから置いた別のファイル");

        var ex = Assert.Throws<InvalidOperationException>(() => _history.Undo());
        Assert.Contains("同じ名前の項目が既にある", ex.Message);
        // 失敗しても中身は動かさない（b.txt は移動されないまま）。
        Assert.True(File.Exists(Path2("b.txt")));
        Assert.Equal("あとから置いた別のファイル", File.ReadAllText(Path2("a.txt")));
    }

    /// <summary>戻せなかった一手は履歴に残す。塞いでいたものを片付けてもう一度 Ctrl+Z、が成り立たないと
    /// 「戻せない」が「消えた」になるうえ、次の Ctrl+Z がひとつ前の無関係な操作に効いてしまう。</summary>
    [Fact]
    public void 戻せなかった一手は履歴に残り片付ければ戻せる()
    {
        File.WriteAllText(Path2("a.txt"), "元");
        _commands.Rename(Path2("a.txt"), "b.txt", isDirectory: false);
        File.WriteAllText(Path2("a.txt"), "邪魔");

        Assert.Throws<InvalidOperationException>(() => _history.Undo());
        Assert.True(_history.CanUndo);
        Assert.Equal("名前の変更「b.txt」", _history.UndoDescription);
        Assert.False(_history.CanRedo);   // 失敗した一手は Redo 側へも移さない

        File.Delete(Path2("a.txt"));
        _history.Undo();
        Assert.Equal("元", File.ReadAllText(Path2("a.txt")));
    }

    /// <summary>作成の Undo→Redo で中身が消えない（作ったあとに書いた内容は「作成」の記録に入っていないので、
    /// 素直に作り直すと空ファイルが返ってきて、書いたものへ戻る道が無くなる）。</summary>
    [Fact]
    public void 作成をやり直すと書き込んだ中身ごと戻る()
    {
        var created = _commands.Create(_root, "note.md", isDirectory: false);
        File.WriteAllText(created, "あとから書いた中身");

        _history.Undo();
        Assert.False(File.Exists(created));

        _history.Redo();
        Assert.Equal("あとから書いた中身", File.ReadAllText(created));
    }

    [Fact]
    public void 対象が消えていると戻せない()
    {
        var created = _commands.Create(_root, "a.txt", isDirectory: false);
        File.Delete(created);

        var ex = Assert.Throws<InvalidOperationException>(() => _history.Undo());
        Assert.Contains("見つかりません", ex.Message);
    }

    [Fact]
    public void 履歴は上限を超えると古いものから捨てる()
    {
        for (var i = 0; i < 60; i++)
            _commands.Create(_root, $"f{i}.txt", isDirectory: false);

        var undone = 0;
        while (_history.CanUndo)
        {
            _history.Undo();
            undone++;
        }
        Assert.Equal(50, undone);
    }

    [Fact]
    public void 大文字小文字だけの名前変更も戻せる()
    {
        File.WriteAllText(Path2("a.txt"), "x");
        _commands.Rename(Path2("a.txt"), "A.txt", isDirectory: false);

        _history.Undo();
        Assert.Equal("a.txt", Directory.EnumerateFiles(_root).Select(Path.GetFileName).Single());
    }

    [Fact]
    public void 削除を元に戻すとゴミ箱から戻る()
    {
        var path = Path2("捨てる.txt");
        File.WriteAllText(path, "戻ってきてほしい中身");

        _commands.Delete(path, isDirectory: false);
        Assert.False(File.Exists(path));
        Assert.Equal("削除「捨てる.txt」", _history.UndoDescription);

        _history.Undo();
        Assert.True(File.Exists(path));
        Assert.Equal("戻ってきてほしい中身", File.ReadAllText(path));

        // やり直すと再びゴミ箱へ。
        _history.Redo();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void フォルダーの削除も中身ごとゴミ箱から戻る()
    {
        Directory.CreateDirectory(Path2("箱", "中"));
        File.WriteAllText(Path2("箱", "中", "x.txt"), "中身");

        _commands.Delete(Path2("箱"), isDirectory: true);
        Assert.False(Directory.Exists(Path2("箱")));

        _history.Undo();
        Assert.Equal("中身", File.ReadAllText(Path2("箱", "中", "x.txt")));
    }
}
