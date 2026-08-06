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

        loop.Invalidate(Caret);         // ②パネルだけの差し替えが走行中
        Assert.Single(seen);            // 1本目が走行中

        loop.Invalidate(Content);       // 走行中に来た要求（内容が変わった＝走行中のものは古い）
        Assert.Single(seen);            // 追い越して2本目を走らせない＝同時1本

        gate.SetResult();
        await loop.Completion;

        // 中断された Caret は捨てられず、あとから来た Content と合流して1回で処理される。
        // （逆向き＝内容の描画中にキャレットが動いた場合は追い越さない。
        //   Caret_request_does_not_throw_away_a_content_render_in_flight を見ること。）
        Assert.Equal([Caret, Content | Caret], seen);
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

    // ── ここから下は「まだ固まる」経路。1本目は追い越しの飢餓、2本目は戻ってこない描画、
    //    3本目は自分以外のトークンで中断された描画。どれも『描かれないまま誰も気づかない』で終わる。

    [Fact]
    public async Task Caret_request_does_not_throw_away_a_content_render_in_flight()
    {
        // キャレット移動では組み立て中の内容は古くならない。ここで捨てていたので、
        // LSP 応答（最長8秒）より短い間隔でキャレットが動くだけで内容が永久に描き変わらなかった。
        var gate = new TaskCompletionSource();
        var seen = new List<EditorSupportUpdateReason>();
        var cancelledDuringContent = false;
        var loop = new EditorSupportUpdateLoop(() => true, async (reason, ct) =>
        {
            seen.Add(reason);
            if (seen.Count > 1)
                return;
            await gate.Task;
            cancelledDuringContent = ct.IsCancellationRequested;
        });

        loop.Invalidate(Content);
        loop.Invalidate(Caret);     // 走行中のキャレット移動

        gate.SetResult();
        await loop.Completion;

        Assert.False(cancelledDuringContent);           // 内容の描画は最後まで走る
        Assert.Equal([Content, Caret], seen);           // キャレットは捨てられず次周回で処理される
    }

    [Fact]
    public async Task Endless_requests_cannot_starve_the_render_forever()
    {
        // 要求が描画より速く来続けても、いつまでも追い越し続けない（一定回数で必ず描き切らせる）。
        // ここが無いと「編集するほど画面が止まる」＝固まる、になる。
        var started = 0;
        var finished = 0;
        EditorSupportUpdateLoop loop = null!;
        loop = new EditorSupportUpdateLoop(() => true, async (_, ct) =>
        {
            started++;
            if (finished == 0)
                loop.Invalidate(Content);   // 描き終わるまで、描画中に必ず次の要求が来る状況
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            finished++;
        });

        loop.Invalidate(Content);
        await WaitUntilAsync(() => finished > 0);
        // 歯止めが無いとドレインは永久に終わらない（＝この待ちが返らない）ので、期限を切って
        // ハングではなくアサート失敗で落とす。
        await Task.WhenAny(loop.Completion, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(finished > 0, $"一度も描き終わらない（started={started}）");
        Assert.True(started <= 4, $"描き切るまでに要求を捨てすぎ（started={started}）");
    }

    [Fact]
    public async Task Render_that_never_answers_cancellation_is_abandoned()
    {
        // WebView2 の初期化や巨大ファイルの読み込みが返らないと、ループは走行中のまま閉じ、
        // 以後どの要求も二度と描かれなかった。中断要求に応答しない描画は見限る。
        var wedged = new TaskCompletionSource();
        var errors = new List<Exception>();
        var runs = 0;
        var loop = new EditorSupportUpdateLoop(() => true, async (_, _) =>
        {
            if (++runs == 1)
                await wedged.Task;   // ct を見ない・永久に返らない描画
        }, errors.Add)
        { AbandonAfterCancel = TimeSpan.FromMilliseconds(50) };

        loop.Invalidate(Content);
        loop.Invalidate(Content);            // 追い越し＝中断を伝える
        await WaitUntilAsync(() => runs >= 2);

        Assert.Equal(2, runs);               // 見限って次の描画が走る
        Assert.False(loop.HasPendingWork);
        Assert.Single(errors);               // 黙って諦めない（診断に出る）
        wedged.SetResult();
    }

    [Fact]
    public void Cancellation_from_an_unrelated_token_is_reported_not_swallowed()
    {
        // 自分のトークン以外で中断された描画（初期化の打ち切り等）を握りつぶすと、
        // 「描かれなかった」ことが誰にも見えないまま古い表示が残る。
        var errors = new List<Exception>();
        var loop = new EditorSupportUpdateLoop(() => true, (_, _) =>
        {
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            unrelated.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, errors.Add);

        loop.Invalidate(Content);

        Assert.Single(errors);
        Assert.False(loop.IsDraining);
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

    /// <summary>条件が成り立つまで待つ（成り立たなければ失敗させるため、待ちきって戻る）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }
}
