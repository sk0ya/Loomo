using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Xunit;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.23 この日のまとめ：コマンド実行の1回ずつの記録（trail.db）と、ローカル日付1日の範囲でのまとめ。</summary>
public sealed class DaySummaryTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static DateTime At(int hour, int minute, int dayOffset = 0)
        => Day.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Local);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-day-summary.db");
    private readonly TrailStore _store;

    public DaySummaryTests() => _store = new TrailStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Command_runs_are_kept_one_by_one_per_workspace_and_day()
    {
        var trail = new TrailViewModel(_store, () => At(18, 0));
        trail.SetWorkspace("ws-a");
        trail.RecordCommandRun("ws-a", new RecentTerminalCommandRun("dotnet test", 1, At(9, 0).ToUniversalTime()));
        trail.RecordCommandRun("ws-a", new RecentTerminalCommandRun(" dotnet test ", 0, At(10, 0).ToUniversalTime()));
        trail.RecordCommandRun("ws-a", new RecentTerminalCommandRun("git log", 0, At(9, 0, dayOffset: -1).ToUniversalTime()));
        trail.RecordCommandRun("ws-b", new RecentTerminalCommandRun("npm test", 0, At(11, 0).ToUniversalTime()));
        trail.RecordCommandRun("ws-a", new RecentTerminalCommandRun("   ", 0, At(12, 0).ToUniversalTime()));

        var runs = trail.LoadCommandRuns(Day);
        Assert.Equal(new[] { ("dotnet test", (int?)1), ("dotnet test", (int?)0) },
            runs.Select(r => (r.Command, r.ExitCode)));
        Assert.Equal(At(9, 0), runs[0].Timestamp);
        Assert.Equal("git log", Assert.Single(trail.LoadCommandRuns(Day.AddDays(-1))).Command);
    }

    [Fact]
    public void Old_trail_db_gains_the_command_table_without_losing_entries()
    {
        var trail = new TrailViewModel(_store, () => At(9, 0));
        trail.EnsureLoaded();
        trail.RecordFile(@"C:\work\a.cs");
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE command_runs;";
            drop.ExecuteNonQuery();
        }

        using var reopened = new TrailStore(_dbPath);
        reopened.AppendCommandRun("", At(10, 0), "dotnet build", 0);
        Assert.Single(reopened.LoadDay("", Day));
        Assert.Single(reopened.LoadCommandRuns("", Day));
    }

    private static CommandRunRecord Run(string command, int? exit, int hour, int minute, int dayOffset = 0)
        => new(At(hour, minute, dayOffset), command, exit);

    private static GitReflogEntry Reflog(DateTime local, string description)
        => new("HEAD", 0, "bbb", "bbb", new DateTimeOffset(local), GitReflogKind.Commit,
            "commit: x", description, "subject", "me") { PreviousHash = "aaa" };

    private static AwayFileChange FileAt(string path, DateTime local)
        => new($"C:\\repo\\{path}", path, new GitChangeEntry(path, null, '.', 'M', false, false), local.ToUniversalTime());

    private static TrailNoteRecord Note(string note, DateTime local)
        => new(1, DateOnly.FromDateTime(local), local, 0, @"C:\work\a.cs", "a.cs", note);

    [Fact]
    public void Same_command_is_grouped_with_counts_and_still_failing_ones_come_first()
    {
        var summary = DaySummaryBuilder.Build(Day,
            Array.Empty<TrailNoteRecord>(),
            new[]
            {
                Run("dotnet test", 1, 9, 0), Run("dotnet test", 0, 11, 30),
                Run("dotnet build", 0, 10, 0), Run("dotnet build", 1, 17, 40),
                Run("git status", 0, 15, 0),
                Run("yesterday", 1, 23, 50, dayOffset: -1),
            },
            Array.Empty<GitReflogEntry>(), Array.Empty<AwayFileChange>());

        Assert.Equal(new[] { "dotnet build", "git status", "dotnet test" }, summary.Items.Select(i => i.Title));
        var build = summary.Items[0];
        Assert.True(build.IsFailure);
        Assert.Equal("✗ 1", build.Badge);
        Assert.Equal("10:00〜17:40・2 回（失敗 1）", build.Detail);
        Assert.Equal("15:00 に終了", summary.Items[1].Detail);
        Assert.Equal("09:00〜11:30・2 回（失敗 1）", summary.Items[2].Detail);
        Assert.False(summary.Items[2].IsFailure);   // 最後の実行は通っている
    }

    [Fact]
    public void Only_that_day_is_listed_with_bookmarks_first()
    {
        var summary = DaySummaryBuilder.Build(Day,
            new[] { Note("ここまで調べた", At(14, 2)), Note("前の日", At(14, 2, dayOffset: -1)) },
            new[] { Run("dotnet test", 0, 9, 0) },
            new[] { Reflog(At(16, 0), "コミット"), Reflog(At(0, 0, dayOffset: 1), "翌日") },
            new[] { FileAt("a.cs", At(13, 0)), FileAt("old.cs", At(13, 0, dayOffset: -1)) });

        Assert.Equal(new[] { AwayItemKind.Bookmark, AwayItemKind.Command, AwayItemKind.Git, AwayItemKind.File },
            summary.Items.Select(i => i.Kind));
        Assert.Equal("ここまで調べた", summary.Items[0].Title);
        Assert.Equal("14:02・a.cs", summary.Items[0].Detail);
        Assert.Equal("コミット", summary.Items[2].Title);
        Assert.Equal("a.cs", summary.Items[3].Title);
        Assert.EndsWith(" のまとめ", summary.Header);
        Assert.Equal("しおり 1・コマンド 1・Git 1・ファイル 1", summary.Counts);
    }

    [Fact]
    public void Markdown_lists_the_card_rows_by_kind()
    {
        var summary = DaySummaryBuilder.Build(Day,
            new[] { Note("再現した", At(14, 2)) },
            new[] { Run("dotnet test", 1, 9, 0) },
            Array.Empty<GitReflogEntry>(), Array.Empty<AwayFileChange>());

        var markdown = summary.ToMarkdown().Replace("\r\n", "\n");
        Assert.StartsWith($"## {summary.Header}\n", markdown);
        Assert.Contains("### しおり\n- 再現した — 14:02・a.cs\n", markdown);
        Assert.Contains("### コマンド\n- `dotnet test` ✗ 1 — 09:00 に終了\n", markdown);
    }

    [Fact]
    public void Away_summary_keeps_its_own_header()
    {
        var since = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
        var summary = new AwaySummary(since, since.AddMinutes(30), Array.Empty<AwayItem>(), 0);
        Assert.Equal("離れていた 30 分のあいだに", summary.Header);
    }
}
