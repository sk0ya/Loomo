using System.IO;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>§27.13 軌跡のしおり：地点へのメモの永続化・同一地点の再通過での保持・日をまたいだ一覧と復帰。</summary>
public sealed class TrailBookmarkTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-trail-bookmark.db");
    private readonly TrailStore _store;

    public TrailBookmarkTests() => _store = new TrailStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Note_survives_reload_and_revisiting_the_same_point()
    {
        var clock = new DateTime(2026, 10, 5, 9, 0, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\a.cs", 3);
        sut.SetNote(sut.Entries[0], "  ここまで調べた \r\n 次は b.cs  ");
        Assert.Equal("ここまで調べた 次は b.cs", sut.Entries[0].Note);
        Assert.True(sut.Entries[0].HasNote);
        Assert.StartsWith("しおり: ここまで調べた", sut.Entries[0].Tooltip);

        // 同じ地点の再通過（デデュープの UPDATE）はメモに触れない
        clock = clock.AddMinutes(5);
        sut.RecordFile(@"C:\work\a.cs", 9);
        Assert.Single(sut.Entries);

        var reloaded = new TrailViewModel(_store, () => clock);
        reloaded.EnsureLoaded();
        Assert.Equal("ここまで調べた 次は b.cs", Assert.Single(reloaded.Entries).Note);
    }

    [Fact]
    public void Empty_note_removes_the_bookmark()
    {
        var sut = new TrailViewModel(_store, () => new DateTime(2026, 10, 5, 9, 0, 0));
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\a.cs");
        sut.SetNote(sut.Entries[0], "memo");
        sut.SetNote(sut.Entries[0], "   ");
        Assert.Null(sut.Entries[0].Note);
        Assert.Empty(sut.ListBookmarks());
    }

    [Fact]
    public void Bookmarks_list_across_days_and_jump_back_to_the_day()
    {
        var clock = new DateTime(2026, 10, 3, 14, 0, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\old.cs");
        sut.RecordFile(@"C:\work\repro.cs");
        sut.SetNote(sut.Entries[1], "この状態で再現した");

        clock = new DateTime(2026, 10, 5, 10, 0, 0);
        var today = new TrailViewModel(_store, () => clock);
        today.EnsureLoaded();
        today.RecordFile(@"C:\work\now.cs");
        today.SetNote(today.Entries[0], "いまここ");

        var bookmarks = today.ListBookmarks();
        Assert.Equal(new[] { "いまここ", "この状態で再現した" }, bookmarks.Select(b => b.Note));
        Assert.Equal(new DateOnly(2026, 10, 3), bookmarks[1].Day);

        TrailEntryViewModel? jumped = null;
        today.JumpRequested += (_, entry) => jumped = entry;
        Assert.True(today.JumpToBookmark(bookmarks[1]));
        Assert.Equal(new DateOnly(2026, 10, 3), today.DisplayDate);
        Assert.Equal(@"C:\work\repro.cs", jumped?.Target);
        Assert.Same(jumped, today.CurrentEntry);
    }

    [Fact]
    public void Bookmarks_are_separated_per_workspace()
    {
        var sut = new TrailViewModel(_store, () => new DateTime(2026, 10, 5, 9, 0, 0));
        sut.EnsureLoaded();
        sut.SetWorkspace("ws-a");
        sut.RecordFile(@"C:\a\x.cs");
        sut.SetNote(sut.Entries[0], "A のしおり");
        sut.SetWorkspace("ws-b");
        Assert.Empty(sut.ListBookmarks());
        sut.SetWorkspace("ws-a");
        Assert.Equal("A のしおり", Assert.Single(sut.ListBookmarks()).Note);
    }

    [Fact]
    public void Old_database_without_the_note_column_is_migrated_in_place()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE trail_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT NOT NULL, timestamp TEXT NOT NULL,
                    kind INTEGER NOT NULL, target TEXT NOT NULL, label TEXT NOT NULL,
                    line INTEGER NOT NULL DEFAULT -1, col INTEGER NOT NULL DEFAULT -1);
                INSERT INTO trail_entries(day, timestamp, kind, target, label)
                VALUES ('2026-10-05', '2026-10-05 09:00:00.000', 0, 'C:\work\a.cs', 'a.cs');
                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new TrailStore(_dbPath);
        var record = Assert.Single(store.LoadDay("", new DateOnly(2026, 10, 5)));
        Assert.Null(record.Note);
        store.SetNote(record.Id, "移行後に付けた");
        Assert.Equal("移行後に付けた", Assert.Single(store.ListNotes("")).Note);
    }
}
