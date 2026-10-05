using System;
using System.IO;
using System.Linq;
using Xunit;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.21 ターミナルの出力を「コマンド1回分」として前回と比べる置き場。</summary>
public sealed class TerminalCommandOutputsTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);

    private static TerminalCommandOutput Run(string command, string text, int? exit = 0, int minutes = 0, bool headOmitted = false)
        => new(command, text, exit, T0.AddMinutes(minutes), headOmitted);

    [Fact]
    public void SameCommandShiftsLatestToPrevious()
    {
        var store = new TerminalCommandOutputs();
        store.Record(Run("dotnet test", "Failed: 1", exit: 1));
        Assert.Empty(store.Pairs);

        store.Record(Run(" dotnet test ", "Passed: 3", minutes: 1));
        var (previous, latest) = Assert.Single(store.Pairs);
        Assert.Equal("Failed: 1", previous.Text);
        Assert.Equal("Passed: 3", latest.Text);
        Assert.Equal("dotnet test", latest.Command);

        // 3回目で一番古いものは押し出される（比べる相手は直前の1回）
        store.Record(Run("dotnet test", "Passed: 4", minutes: 2));
        (previous, latest) = Assert.Single(store.Pairs);
        Assert.Equal("Passed: 3", previous.Text);
        Assert.Equal("Passed: 4", latest.Text);
    }

    [Fact]
    public void NewestCommandComesFirstAndOldestKindsAreDropped()
    {
        var store = new TerminalCommandOutputs();
        for (int i = 0; i < TerminalCommandOutputs.MaxCommands + 3; i++)
            store.Record(Run($"cmd{i}", $"out{i}", minutes: i));
        store.Record(Run("cmd5", "again", minutes: 100));

        var latest = store.Latest;
        Assert.Equal(TerminalCommandOutputs.MaxCommands, latest.Count);
        Assert.Equal("cmd5", latest[0].Command);
        Assert.DoesNotContain(latest, o => o.Command == "cmd0");
    }

    [Fact]
    public void EmptyCommandIsIgnored()
    {
        var store = new TerminalCommandOutputs();
        store.Record(Run("  ", "x"));
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void OversizedOutputKeepsTheTailFromALineStart()
    {
        var store = new TerminalCommandOutputs();
        var lines = Enumerable.Range(0, 200_000).Select(i => $"line {i}");
        store.Record(Run("big", string.Join("\n", lines)));

        var output = Assert.Single(store.Latest);
        Assert.True(output.HeadOmitted);
        Assert.True(output.Text.Length <= TerminalCommandOutputs.MaxChars);
        Assert.StartsWith("line ", output.Text);
        Assert.EndsWith("line 199999", output.Text);
    }

    [Fact]
    public void ComparisonPutsPreviousOnTheLeft()
    {
        var comparison = TerminalCommandOutputs.Compare(
            Run("dotnet test", "old", exit: 1), Run("dotnet test", "new", exit: 0, headOmitted: true));

        Assert.Equal("old", comparison.LeftText);
        Assert.Equal("new", comparison.RightText);
        Assert.StartsWith("前回 ", comparison.LeftTitle);
        Assert.Contains("✗ 1", comparison.LeftTitle);
        Assert.StartsWith("今回 ", comparison.RightTitle);
        Assert.Contains("✓", comparison.RightTitle);
        Assert.Contains("先頭省略", comparison.RightTitle);
        Assert.Equal("", comparison.FilePath);   // 出どころのファイルは無い＝行へ飛ばない
    }

    [Fact]
    public void DocumentNameUsesTheFirstWordAndIsAValidFileName()
    {
        var name = TerminalCommandOutputs.DocumentName(Run("git:log --oneline", "x"));
        Assert.StartsWith("出力-git_log-", name);
        Assert.EndsWith(".log", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }
}
