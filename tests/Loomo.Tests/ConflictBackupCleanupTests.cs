using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>上書き貼り付けで退避した実体（<c>.loomo-conflict-*</c>）が、いつ消えるか。
/// Undo のために残すのは正しいが、<b>残しっぱなしにしない</b>ことがここの主題。</summary>
public sealed class ConflictBackupCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-backup-cleanup-{Guid.NewGuid():N}");
    private readonly string _journal;
    private readonly FileOperationHistory _history = new();
    private readonly FolderTreeCommandHandler _commands;

    public ConflictBackupCleanupTests()
    {
        Directory.CreateDirectory(_root);
        _journal = Path.Combine(_root, "pending-conflicts.txt");
        ConflictBackupJournal.UseFile(_journal);
        _commands = FolderTreeCommandHandler.Unconfined(new FakeWorkspaceService(), _history);
    }

    public void Dispose()
    {
        ConflictBackupJournal.UseFile(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private string[] Backups() => Directory
        .GetFileSystemEntries(P("dst"), ConflictBackupJournal.Prefix + "*");

    private void Overwrite()
    {
        Directory.CreateDirectory(P("dst"));
        File.WriteAllText(P("source.txt"), "new");
        File.WriteAllText(P("dst", "source.txt"), "old");
        _commands.PasteWithConflict(P("dst"), P("source.txt"), move: false,
            _ => new FileConflictDecision(FileConflictAction.Overwrite));
    }

    [Fact]
    public void 履歴を捨てると退避してあった実体も消える()
    {
        Overwrite();
        // Undo で戻せるよう、上書きされた「old」は隠しコピーとして残っている。
        Assert.Single(Backups());

        _history.Clear();

        Assert.Empty(Backups());
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public void 履歴に載っている間は退避を消さない()
    {
        Overwrite();
        Assert.Single(Backups());

        _history.Undo();

        Assert.Equal("old", File.ReadAllText(P("dst", "source.txt")));
        Assert.Empty(Backups());   // 戻したので退避先そのものが無くなる
    }

    [Fact]
    public void 前回のプロセスが残した退避は起動時の掃除で消える()
    {
        Overwrite();
        var backup = Assert.Single(Backups());
        // 履歴を捨てないままプロセスが落ちた状況＝台帳だけがディスクに残る。
        Assert.Contains(backup, File.ReadAllLines(_journal));

        ConflictBackupJournal.Sweep();

        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public void 台帳に紛れ込んだ無関係なパスは消さない()
    {
        var innocent = P("大事なファイル.txt");
        File.WriteAllText(innocent, "keep");
        File.WriteAllLines(_journal, [innocent]);

        ConflictBackupJournal.Sweep();

        Assert.True(File.Exists(innocent));
    }
}
