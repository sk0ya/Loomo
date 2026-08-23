using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class FileConflictResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-conflict-{Guid.NewGuid():N}");
    private readonly FileOperationHistory _history = new();
    private readonly FolderTreeCommandHandler _commands;

    public FileConflictResolutionTests()
    {
        Directory.CreateDirectory(_root);
        _commands = FolderTreeCommandHandler.Unconfined(new FakeWorkspaceService(), _history);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void 上書きは元のファイルをUndoで復元しRedoでも再適用できる()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source.txt"), "new");
        File.WriteAllText(P("dst", "source.txt"), "old");

        var result = _commands.PasteWithConflict(P("dst"), P("source.txt"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Overwrite));

        Assert.Equal(P("dst", "source.txt"), result.DestinationPath);
        Assert.Equal("new", File.ReadAllText(P("dst", "source.txt")));
        Assert.Equal("コピー「source.txt」", _history.UndoDescription);

        _history.Undo();
        Assert.Equal("old", File.ReadAllText(P("dst", "source.txt")));

        _history.Redo();
        Assert.Equal("new", File.ReadAllText(P("dst", "source.txt")));
    }

    [Fact]
    public void 移動の上書きはUndoで移動元と既存先を両方戻す()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source.txt"), "new");
        File.WriteAllText(P("dst", "source.txt"), "old");

        _commands.PasteWithConflict(P("dst"), P("source.txt"), move: true,
            _ => new FileConflictDecision(FileConflictAction.Overwrite));
        Assert.False(File.Exists(P("source.txt")));
        Assert.Equal("new", File.ReadAllText(P("dst", "source.txt")));

        _history.Undo();
        Assert.Equal("new", File.ReadAllText(P("source.txt")));
        Assert.Equal("old", File.ReadAllText(P("dst", "source.txt")));

        _history.Redo();
        Assert.False(File.Exists(P("source.txt")));
        Assert.Equal("new", File.ReadAllText(P("dst", "source.txt")));
    }

    [Fact]
    public void 名前変更は指定名へ保存し元の競合項目を残す()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source.txt"), "new");
        File.WriteAllText(P("dst", "source.txt"), "old");

        var result = _commands.PasteWithConflict(P("dst"), P("source.txt"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Rename, "renamed.txt"));

        Assert.Equal(P("dst", "renamed.txt"), result.DestinationPath);
        Assert.Equal("old", File.ReadAllText(P("dst", "source.txt")));
        Assert.Equal("new", File.ReadAllText(P("dst", "renamed.txt")));
    }

    [Fact]
    public void スキップとキャンセルはファイルと履歴を変更しない()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source.txt"), "new");
        File.WriteAllText(P("dst", "source.txt"), "old");

        var skipped = _commands.PasteWithConflict(P("dst"), P("source.txt"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Skip));
        Assert.True(skipped.Skipped);
        Assert.Equal("old", File.ReadAllText(P("dst", "source.txt")));
        Assert.False(_history.CanUndo);

        var cancelled = _commands.PasteWithConflict(P("dst"), P("source.txt"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Cancel));
        Assert.True(cancelled.Cancelled);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void フォルダーの上書きも中身ごとUndoRedoできる()
    {
        Directory.CreateDirectory(P("dst", "folder", "old-child"));
        Directory.CreateDirectory(P("source", "folder", "new-child"));
        File.WriteAllText(P("source", "folder", "new-child", "new.txt"), "new");
        File.WriteAllText(P("dst", "folder", "old-child", "old.txt"), "old");

        _commands.PasteWithConflict(P("dst"), P("source", "folder"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Overwrite));
        Assert.True(File.Exists(P("dst", "folder", "new-child", "new.txt")));
        Assert.False(File.Exists(P("dst", "folder", "old-child", "old.txt")));

        _history.Undo();
        Assert.True(File.Exists(P("dst", "folder", "old-child", "old.txt")));
        Assert.False(File.Exists(P("dst", "folder", "new-child", "new.txt")));

        _history.Redo();
        Assert.True(File.Exists(P("dst", "folder", "new-child", "new.txt")));
    }

    [Fact]
    public void ファイルでフォルダーを上書きしてもUndoRedoできる()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source"), "new");
        Directory.CreateDirectory(P("dst", "source"));
        File.WriteAllText(P("dst", "source", "old.txt"), "old");

        _commands.PasteWithConflict(P("dst"), P("source"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Overwrite));
        Assert.Equal("new", File.ReadAllText(P("dst", "source")));

        _history.Undo();
        Assert.True(Directory.Exists(P("dst", "source")));
        Assert.Equal("old", File.ReadAllText(P("dst", "source", "old.txt")));

        _history.Redo();
        Assert.Equal("new", File.ReadAllText(P("dst", "source")));
    }

    [Fact]
    public void 退避ファイルはFolderTreeの表示対象にならない()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source.txt"), "new");
        File.WriteAllText(P("dst", "source.txt"), "old");

        _commands.PasteWithConflict(P("dst"), P("source.txt"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Overwrite));

        var children = new FolderTreeQuery().EnumerateChildren(P("dst"));
        Assert.DoesNotContain(children.Files, path => Path.GetFileName(path).StartsWith(".loomo-conflict-", StringComparison.OrdinalIgnoreCase));
        _history.Clear();
    }
}
