using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.71 動いているもの面の行を「あとで」に回す——下へ退けて人の番から外し、状態が変わったら戻す。</summary>
public sealed class RunningSnoozeTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);

    private static RunningEntry Claude(string tab, string label, bool attention)
        => new($"claude:{tab}", RunningEntryKind.Claude, $"Claude Code — {tab}", label, Now,
            attention ? RunningEntryTone.Attention : RunningEntryTone.Normal, CanStop: !attention,
            attention ? RunningEntries.AttentionGroup : RunningEntries.RunningGroup);

    private static RunningEntry Server()
        => new("lsp:x", RunningEntryKind.LanguageServer, "roslyn", "app · 準備完了", null,
            RunningEntryTone.Quiet, false, RunningEntries.ResidentGroup);

    [Fact]
    public void Snoozed_row_moves_below_everything_and_leaves_the_attention_count()
    {
        var model = new RunningViewModel();
        model.Apply([Claude("a", "入力待ち", true), Claude("b", "入力待ち", true), Server()], Now);
        Assert.Equal(2, model.AttentionCount);
        var raised = 0;
        model.AttentionChanged += (_, _) => raised++;

        model.SnoozeCommand.Execute(model.Items.First(i => i.Key == "claude:a"));

        Assert.Equal(1, model.AttentionCount);
        Assert.Equal(1, raised);
        var last = model.Items.Last();
        Assert.Equal("claude:a", last.Key);
        Assert.True(last.IsSnoozed);
        Assert.Equal(RunningEntries.SnoozedGroup, last.Group);
    }

    [Fact]
    public void A_changed_state_brings_the_row_back()
    {
        var model = new RunningViewModel();
        model.Apply([Claude("a", "入力待ち", true)], Now);
        model.SnoozeCommand.Execute(model.Items.Single());

        model.Apply([Claude("a", "入力待ち", true)], Now.AddMinutes(5));   // 同じ状態のまま（時間だけ経った）
        Assert.True(model.Items.Single().IsSnoozed);

        model.Apply([Claude("a", "作業中", false)], Now.AddMinutes(6));    // 動き出した
        Assert.False(model.Items.Single().IsSnoozed);

        model.Apply([Claude("a", "入力待ち", true)], Now.AddMinutes(9));   // また待っている＝新しい知らせ
        Assert.False(model.Items.Single().IsSnoozed);
        Assert.Equal(1, model.AttentionCount);
    }

    [Fact]
    public void Unsnooze_and_vanished_rows_forget_the_snooze()
    {
        var snoozed = new Dictionary<string, string>();
        var entry = Claude("a", "入力待ち", true);
        snoozed[entry.Key] = RunningEntries.SnoozeSignature(entry);

        var rows = RunningEntries.ApplySnooze([Server()], snoozed);   // 「あとで」にした行はもう居ない
        Assert.Equal("lsp:x", Assert.Single(rows).Key);
        Assert.Empty(snoozed);

        var model = new RunningViewModel();
        model.Apply([entry], Now);
        model.SnoozeCommand.Execute(model.Items.Single());
        model.UnsnoozeCommand.Execute(model.Items.Single());
        Assert.False(model.Items.Single().IsSnoozed);
        Assert.Equal(1, model.AttentionCount);
    }
}
