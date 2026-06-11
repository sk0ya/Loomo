using System;
using System.IO;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// AI入力欄の履歴ナビゲーション（<see cref="PromptInputHistory"/>）の検証。
/// ↑/↓ のカーソル移動・下書き退避・重複抑止・上限切り詰め・永続化の引き継ぎを固定する。
/// </summary>
public sealed class PromptInputHistoryTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), "loomo-history-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* 後始末の失敗は無視 */ }
    }

    private PromptInputHistory Create(int maxEntries = 200)
        => new(new PromptHistoryStore(_file, maxEntries));

    [Fact]
    public void RecallPrevious_returns_false_when_history_is_empty()
    {
        var history = Create();
        Assert.False(history.RecallPrevious("draft", out _));
    }

    [Fact]
    public void RecallPrevious_walks_back_and_stops_at_oldest()
    {
        var history = Create();
        history.Push("first");
        history.Push("second");

        Assert.True(history.RecallPrevious("draft", out var r1));
        Assert.Equal("second", r1);
        Assert.True(history.RecallPrevious("draft", out var r2));
        Assert.Equal("first", r2);

        // 最古に達したら、キーは消費するが内容は変えない（null）
        Assert.True(history.RecallPrevious("draft", out var r3));
        Assert.Null(r3);
    }

    [Fact]
    public void RecallNext_returns_to_draft_after_walking_past_newest()
    {
        var history = Create();
        history.Push("first");
        history.Push("second");

        history.RecallPrevious("編集中の下書き", out _); // → second
        history.RecallPrevious("編集中の下書き", out _); // → first

        Assert.True(history.RecallNext(out var forward));
        Assert.Equal("second", forward);

        // 末尾を超えたらナビ開始時の下書きへ戻る
        Assert.True(history.RecallNext(out var draft));
        Assert.Equal("編集中の下書き", draft);

        // ナビ終了後の ↓ は素通し
        Assert.False(history.RecallNext(out _));
    }

    [Fact]
    public void ResetNavigation_makes_next_recall_start_from_newest()
    {
        var history = Create();
        history.Push("first");
        history.Push("second");

        history.RecallPrevious("d", out _); // → second
        history.ResetNavigation();          // 手入力相当

        Assert.True(history.RecallPrevious("typed", out var r));
        Assert.Equal("second", r); // 最新からやり直し
    }

    [Fact]
    public void Push_skips_blank_and_consecutive_duplicates()
    {
        var history = Create();
        history.Push("same");
        history.Push("same");   // 直前と同一→積まない
        history.Push("   ");    // 空白のみ→積まない

        Assert.True(history.RecallPrevious("", out var r1));
        Assert.Equal("same", r1);
        Assert.True(history.RecallPrevious("", out var r2));
        Assert.Null(r2); // 1件しか無い
    }

    [Fact]
    public void Push_trims_to_max_entries()
    {
        var history = Create(maxEntries: 2);
        history.Push("a");
        history.Push("b");
        history.Push("c"); // "a" が落ちる

        history.RecallPrevious("", out var r1);
        Assert.Equal("c", r1);
        history.RecallPrevious("", out var r2);
        Assert.Equal("b", r2);
        Assert.True(history.RecallPrevious("", out var r3));
        Assert.Null(r3); // "a" はもう無い
    }

    [Fact]
    public void History_persists_across_instances_via_store()
    {
        Create().Push("persisted");

        var second = Create();
        Assert.True(second.RecallPrevious("", out var r));
        Assert.Equal("persisted", r);
    }
}
