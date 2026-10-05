using System;
using System.Linq;
using Xunit;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.22 留守中に起きたこと：離席の判定と、離れた時刻以降の記録のまとめ。</summary>
public sealed class AwaySummaryTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShortDeactivationIsNotAway()
    {
        var presence = new PresenceTracker();
        presence.OnDeactivated(T0);
        Assert.Null(presence.OnReturn(T0.AddMinutes(2)));
        Assert.False(presence.IsAway);
    }

    [Fact]
    public void LongDeactivationReportsWhenItStarted()
    {
        var presence = new PresenceTracker();
        presence.OnDeactivated(T0);
        presence.OnDeactivated(T0.AddMinutes(1));   // 早い方を残す
        Assert.Equal(T0, presence.OnReturn(T0.AddMinutes(20)));
        Assert.Null(presence.OnReturn(T0.AddMinutes(21)));   // 1回だけ
    }

    [Fact]
    public void IdleInForegroundStartsAtTheLastInput()
    {
        var presence = new PresenceTracker();
        var lastInput = T0.AddMinutes(1);
        presence.OnIdleCheck(T0.AddMinutes(4), lastInput);
        Assert.False(presence.IsAway);   // まだ5分経っていない

        presence.OnIdleCheck(T0.AddMinutes(7), lastInput);
        Assert.True(presence.IsAway);
        Assert.Equal(lastInput, presence.OnReturn(T0.AddMinutes(30)));
    }

    private static RecentTerminalCommandSnapshot Cmd(string command, int? exit, int minutes)
        => new() { Command = command, ExitCode = exit, LastRunUtc = T0.AddMinutes(minutes) };

    private static GitReflogEntry Reflog(int minutes, string description, string? previous = "aaa", string hash = "bbb")
        => new("HEAD", 0, hash, hash, new DateTimeOffset(T0.AddMinutes(minutes)), GitReflogKind.Commit,
            "commit: x", description, "subject", "me") { PreviousHash = previous };

    private static AwayFileChange File(string path, int minutes, bool untracked = false)
        => new($"C:\\repo\\{path}", path, new GitChangeEntry(path, null, untracked ? '?' : '.', untracked ? '?' : 'M', untracked, false),
            T0.AddMinutes(minutes));

    [Fact]
    public void OnlyThingsAfterLeavingAreListedFailuresFirst()
    {
        var summary = AwaySummaryBuilder.Build(T0, T0.AddMinutes(40),
            new[] { Cmd("dotnet build", 0, 10), Cmd("dotnet test", 1, 5), Cmd("git status", 0, -3) },
            new[] { Reflog(12, "コミット"), Reflog(-10, "前のコミット") },
            new[] { File("a.cs", 8), File("old.cs", -1), File("new.txt", 9, untracked: true) });

        Assert.Equal(new[] { "dotnet test", "dotnet build" },
            summary.Items.Where(i => i.Kind == AwayItemKind.Command).Select(i => i.Title));
        Assert.True(summary.Items.First().IsFailure);
        Assert.Equal("✗ 1", summary.Items.First().Badge);
        Assert.Equal("コミット", Assert.Single(summary.Items, i => i.Kind == AwayItemKind.Git).Title);
        Assert.Equal(new[] { "new.txt", "a.cs" },
            summary.Items.Where(i => i.Kind == AwayItemKind.File).Select(i => i.Title));
        Assert.Contains("新規", summary.Items.First(i => i.Title == "new.txt").Detail);
        Assert.Equal("離れていた 40 分のあいだに", summary.Header);
        Assert.Equal("コマンド 2（失敗 1）・Git 1・ファイル 2", summary.Counts);
    }

    [Fact]
    public void NothingAfterLeavingIsEmpty()
    {
        var summary = AwaySummaryBuilder.Build(T0, T0.AddMinutes(10),
            new[] { Cmd("ls", 0, -1) }, Array.Empty<GitReflogEntry>(), new[] { File("a.cs", -2) });
        Assert.True(summary.IsEmpty);
    }

    [Fact]
    public void OverflowIsCountedNotListed()
    {
        var files = Enumerable.Range(0, AwaySummaryBuilder.MaxFiles + 3).Select(i => File($"f{i}.cs", i + 1));
        var summary = AwaySummaryBuilder.Build(T0, T0.AddHours(2), Array.Empty<RecentTerminalCommandSnapshot>(),
            Array.Empty<GitReflogEntry>(), files);
        Assert.Equal(AwaySummaryBuilder.MaxFiles, summary.Items.Count);
        Assert.Equal(3, summary.HiddenCount);
        Assert.EndsWith("ほか 3", summary.Counts);
        Assert.Equal("離れていた 2 時間のあいだに", summary.Header);
    }
}
