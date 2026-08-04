using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// EditorSupport の更新ループが守る不変条件のテスト。ここが崩れると
/// 「ペインの中身が古いまま更新されなくなる（固まる）」が再発する。
/// </summary>
public class EditorSupportUpdateLoopTests
{
    private const EditorSupportUpdateReason Content = EditorSupportUpdateReason.Content;
    private const EditorSupportUpdateReason Caret = EditorSupportUpdateReason.Caret;

    [Fact]
    public void Request_while_hidden_is_kept_and_rendered_once_when_visible()
    {
        var visible = false;
        var renders = 0;
        var loop = new EditorSupportUpdateLoop(() => visible, (_, _) => { renders++; return Task.CompletedTask; });

        loop.Invalidate(Content);

        Assert.Equal(0, renders);
        Assert.True(loop.HasPendingWork);   // 捨てずに残す＝ dirty

        visible = true;
        loop.Invalidate(Content);

        Assert.Equal(1, renders);
        Assert.False(loop.HasPendingWork);
    }

    [Fact]
    public async Task Request_arriving_during_a_render_is_not_lost()
    {
        var gate = new TaskCompletionSource();
        var seen = new List<EditorSupportUpdateReason>();
        var loop = new EditorSupportUpdateLoop(() => true, async (reason, _) =>
        {
            seen.Add(reason);
            if (seen.Count == 1)
                await gate.Task;
        });

        loop.Invalidate(Content);
        Assert.Single(seen);            // 1本目が走行中

        loop.Invalidate(Caret);         // 走行中に来た要求
        Assert.Single(seen);            // 追い越して2本目を走らせない＝同時1本

        gate.SetResult();
        await loop.Completion;

        // 中断された Content は捨てられず、あとから来た Caret と合流して1回で処理される。
        Assert.Equal([Content, Content | Caret], seen);
        Assert.False(loop.HasPendingWork);
    }

    [Fact]
    public async Task New_request_cancels_the_render_in_flight()
    {
        var gate = new TaskCompletionSource();
        var cancelled = false;
        var runs = 0;
        var loop = new EditorSupportUpdateLoop(() => true, async (_, ct) =>
        {
            runs++;
            if (runs > 1)
                return;
            await gate.Task;
            cancelled = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
        });

        loop.Invalidate(Content);
        loop.Invalidate(Content);
        gate.SetResult();
        await loop.Completion;

        Assert.True(cancelled);   // 走行中の描画には「もう古い」と伝わる
        Assert.Equal(2, runs);    // そのうえでやり直される
    }

    [Fact]
    public void Failing_render_does_not_wedge_the_loop()
    {
        var errors = new List<Exception>();
        var runs = 0;
        var loop = new EditorSupportUpdateLoop(() => true, (_, _) =>
        {
            runs++;
            if (runs == 1)
                throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }, errors.Add);

        loop.Invalidate(Content);
        Assert.Single(errors);
        Assert.False(loop.IsDraining);

        loop.Invalidate(Content);
        Assert.Equal(2, runs);   // 次の要求は普通に処理される
    }

    [Fact]
    public void Invalidating_from_inside_a_render_reruns_exactly_once()
    {
        // WebView の本文差し替え先が消えていた場合、適用中に「ページ全体で組み直せ」と
        // 自分へ要求を投げ返す。ここが無限ループにならないことを保証する。
        var runs = 0;
        EditorSupportUpdateLoop loop = null!;
        loop = new EditorSupportUpdateLoop(() => true, (_, _) =>
        {
            runs++;
            if (runs == 1)
                loop.Invalidate(Content);
            return Task.CompletedTask;
        });

        loop.Invalidate(Content);

        Assert.Equal(2, runs);
        Assert.False(loop.HasPendingWork);
    }

    [Fact]
    public void None_is_ignored()
    {
        var renders = 0;
        var loop = new EditorSupportUpdateLoop(() => true, (_, _) => { renders++; return Task.CompletedTask; });

        loop.Invalidate(EditorSupportUpdateReason.None);

        Assert.Equal(0, renders);
        Assert.False(loop.HasPendingWork);
    }
}
