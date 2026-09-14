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
        var loop = Loop(() => visible, (_, _) => { renders++; return Task.CompletedTask; });

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
        var loop = Loop(() => true, async (reason, _) =>
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
        var loop = Loop(() => true, async (_, ct) =>
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
        var loop = Loop(() => true, (_, _) =>
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
        loop = Loop(() => true, (_, _) =>
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
        var loop = Loop(() => true, async (reason, ct) =>
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
        loop = Loop(() => true, async (_, ct) =>
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
        var loop = Loop(() => true, async (_, _) =>
        {
            if (++runs == 1)
                await wedged.Task;   // ct を見ない・永久に返らない描画
        }, errors.Add);
        loop.AbandonAfterCancel = TimeSpan.FromMilliseconds(50);

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
        var loop = Loop(() => true, (_, _) =>
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
        var loop = Loop(() => true, (_, _) => { renders++; return Task.CompletedTask; });

        loop.Invalidate(EditorSupportUpdateReason.None);

        Assert.Equal(0, renders);
        Assert.False(loop.HasPendingWork);
    }

    // ── 描けない間に持ち越した要求を、誰にも知らせてもらわずに回収する ──────────
    //    ここが無いと「要求は消えない」が絵に描いた餅になる。要求を持っていても、それを
    //    描かせる Invalidate が来ない可視化経路（俯瞰の開閉・舞台からタイルへ戻る・袖から戻す…）が
    //    1つでもあれば「ペインは見えているのに中身が古いまま」が残る。

    [Fact]
    public void 描けない間に残した要求は誰も知らせなくても自分で拾い直す()
    {
        var visible = false;
        var renders = 0;
        var watch = new FakeWatch();
        var loop = Loop(() => visible, (_, _) => { renders++; return Task.CompletedTask; }, watch: watch);

        loop.Invalidate(Content);
        Assert.Equal(0, renders);
        Assert.True(watch.IsArmed);      // 描けないので見回りが仕掛かる

        watch.Fire();                    // まだ描けない
        Assert.Equal(0, renders);
        Assert.True(watch.IsArmed);      // 諦めずにまた見に来る

        visible = true;
        watch.Fire();                    // 誰も Invalidate していないのに——

        Assert.Equal(1, renders);        // 描かれる
        Assert.False(loop.HasPendingWork);
        Assert.False(watch.IsArmed);     // 用が済んだら止まる
    }

    [Fact]
    public void 見回りは間隔を空けていき最後は頭打ちになる()
    {
        // ペインを閉じたまま何時間も置かれる場合に、細かい起床を延々と残さない。
        var watch = new FakeWatch();
        var loop = Loop(() => false, (_, _) => Task.CompletedTask, watch: watch);

        loop.Invalidate(Content);
        for (var i = 0; i < 4; i++)
            watch.Fire();

        var delays = EditorSupportUpdateLoop.RenderabilityPollDelays;
        Assert.Equal(
            [delays[0], delays[1], delays[2], delays[3], delays[^1]],
            watch.Delays);
    }

    [Fact]
    public void 要求が積み直されただけでは見回りの間隔は空かない()
    {
        // ペインを閉じたまま編集・タブ切替を続けると Invalidate が何度も届く。これで段階を
        // 進めてしまうと、4回で終端の30秒へ飛ぶ＝可視化の合図を取りこぼした経路で、古い内容が
        // 250ms ではなく30秒居座る。細かい刻みは<b>いちばん普通の場合にこそ</b>使うためにある。
        var watch = new FakeWatch();
        var loop = Loop(() => false, (_, _) => Task.CompletedTask, watch: watch);

        for (var i = 0; i < 5; i++)
            loop.Invalidate(Content);

        var first = EditorSupportUpdateLoop.RenderabilityPollDelays[0];
        Assert.Equal([first, first, first, first, first], watch.Delays);

        // 空振りした見回りだけが段階を進める。
        watch.Fire();
        Assert.Equal(EditorSupportUpdateLoop.RenderabilityPollDelays[1], watch.Delays[^1]);
    }

    [Fact]
    public void 見回りは間隔が伸びきっても止まらない()
    {
        // 止めてしまうと、要求の回収が「可視化した側が知らせに来る」前提へ戻る＝この網の意味が消える。
        var visible = false;
        var watch = new FakeWatch();
        var renders = 0;
        var loop = Loop(() => visible, (_, _) => { renders++; return Task.CompletedTask; }, watch: watch);

        loop.Invalidate(Content);
        for (var i = 0; i < 20; i++)
            watch.Fire();
        Assert.True(watch.IsArmed);

        visible = true;
        watch.Fire();

        Assert.Equal(1, renders);   // 何時間後でも、可視化されれば描かれる
    }

    [Fact]
    public void 描き切ったら見回りの間隔もやり直しになる()
    {
        var visible = false;
        var watch = new FakeWatch();
        var loop = Loop(() => visible, (_, _) => Task.CompletedTask, watch: watch);

        loop.Invalidate(Content);
        watch.Fire();                    // 間隔が1段進む
        visible = true;
        watch.Fire();                    // ここで描き切る
        visible = false;
        loop.Invalidate(Content);        // 次に描けなくなったとき——

        Assert.Equal(
            [EditorSupportUpdateLoop.RenderabilityPollDelays[0]], watch.Delays[^1..]);
    }

    [Fact]
    public void 可視状態の問い合わせは要求が無ければ何もしない()
    {
        // レイアウトの組み直しから無条件に呼ばれる。要求が無いのに描き直すと、
        // ペインを触るたびにプレビュー全体が作り直されることになる。
        var renders = 0;
        var watch = new FakeWatch();
        var loop = Loop(() => true, (_, _) => { renders++; return Task.CompletedTask; }, watch: watch);

        loop.PollRenderability();

        Assert.Equal(0, renders);
        Assert.False(watch.IsArmed);
    }

    [Fact]
    public void 可視化の合図で持ち越した要求がその場で描かれる()
    {
        // 見回りを待たずに描くための早道（レイアウト組み直しからの合図）。
        var visible = false;
        var renders = 0;
        var watch = new FakeWatch();
        var loop = Loop(() => visible, (_, _) => { renders++; return Task.CompletedTask; }, watch: watch);
        loop.Invalidate(Content);
        visible = true;

        loop.PollRenderability();

        Assert.Equal(1, renders);
        Assert.False(loop.HasPendingWork);
        Assert.False(watch.IsArmed);
    }

    [Fact]
    public async Task 走行中に来た可視化の合図は二重に描かない()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var watch = new FakeWatch();
        var loop = Loop(() => true, async (_, _) =>
        {
            if (++runs == 1)
                await gate.Task;
        }, watch: watch);

        loop.Invalidate(Content);
        loop.PollRenderability();        // 走行中：ドレインが面倒を見るので何も起こさない
        Assert.Equal(1, runs);

        gate.SetResult();
        await loop.Completion;
        Assert.Equal(1, runs);
    }

    // ── 戻ってこない描画が続く（ワークスペース切替で WebView2・言語サーバーの足場が消える）──────
    //    見限りを1回だけ確かめていた頃は、2回目以降に何が起きるかを誰も見ていなかった。

    [Fact]
    public async Task 戻ってこない描画が何度続いてもループは永久には止まらない()
    {
        // 見限った回数が「追い越し回数」に数えられ、しかも描き切るまで0に戻らない。
        // 2回見限ったあとは次の描画を止められなくなり、それがまた戻ってこないと
        // ループは走行中のまま閉じる——以後どの要求も二度と描かれない（＝固まる）。
        var wedged = new TaskCompletionSource();
        var runs = 0;
        var finished = 0;
        var loop = Loop(() => true, async (_, _) =>
        {
            if (++runs <= 3)
                await wedged.Task;   // ct を見ない・永久に返らない
            finished++;
        });
        loop.AbandonAfterCancel = TimeSpan.FromMilliseconds(30);
        loop.StaleRenderDeadline = TimeSpan.FromMilliseconds(30);

        loop.Invalidate(Content);
        for (var expected = 2; expected <= 4; expected++)
        {
            loop.Invalidate(Content);            // 描けないまま次の要求が来る
            await WaitUntilAsync(() => runs >= expected);
        }
        await WaitUntilAsync(() => !loop.IsDraining);

        Assert.Equal(4, runs);
        Assert.Equal(1, finished);
        Assert.False(loop.IsDraining);
        Assert.False(loop.HasPendingWork);
        wedged.SetResult();
    }

    [Fact]
    public async Task キャレットの要求で止めない内容の描画も戻ってこなければ見限る()
    {
        // 内容の描画はキャレット移動では止めない。でもそれが戻ってこないなら、待っている
        // キャレットの要求は永久に処理されない。止めない特例にも期限が要る。
        var wedged = new TaskCompletionSource();
        var seen = new List<EditorSupportUpdateReason>();
        var loop = Loop(() => true, async (reason, _) =>
        {
            seen.Add(reason);
            if (seen.Count == 1)
                await wedged.Task;
        });
        loop.AbandonAfterCancel = TimeSpan.FromMilliseconds(30);
        loop.StaleRenderDeadline = TimeSpan.FromMilliseconds(30);

        loop.Invalidate(Content);
        loop.Invalidate(Caret);
        await WaitUntilAsync(() => seen.Count >= 2);

        Assert.Equal([Content, Content | Caret], seen);   // 見限った内容の要求も捨てずに一緒に描く
        wedged.SetResult();
    }

    [Fact]
    public void 期限は古くなった描画だけに付き_最新の要求を描いている描画は急かさない()
    {
        // 大きな Excel・巨大 CSV の読み込みは長い。誰も待っていない描画を時間で打ち切ると、
        // 重いファイルは永久に表示できなくなる。
        var gate = new TaskCompletionSource();
        CancellationToken token = default;
        var loop = Loop(() => true, async (_, ct) =>
        {
            token = ct;
            await gate.Task;
        });
        loop.StaleRenderDeadline = TimeSpan.FromMilliseconds(1);

        loop.Invalidate(Content);
        Thread.Sleep(50);

        Assert.False(token.IsCancellationRequested);
        gate.SetResult();
    }

    [Fact]
    public async Task 上限に達しても見限りが無ければ遅い描画を時間で打ち切らない()
    {
        // 巨大 CSV・Excel の読み込みは期限を超えうる。追い越しの上限に達しただけで期限を付けると、
        // 編集を続ける限り毎回打ち切られて永久に表示されない。期限を付けるのは見限りが起きた後だけ。
        var slow = new TaskCompletionSource();
        var tokens = new List<CancellationToken>();
        var loop = Loop(() => true, async (_, ct) =>
        {
            tokens.Add(ct);
            if (tokens.Count <= 2)
                await Task.Delay(Timeout.Infinite, ct);   // 追い越されれば素直に止まる
            else
                await slow.Task;                          // 遅いが生きている
        });
        loop.StaleRenderDeadline = TimeSpan.FromMilliseconds(1);

        loop.Invalidate(Content);
        loop.Invalidate(Content);
        await WaitUntilAsync(() => tokens.Count >= 2);
        loop.Invalidate(Content);
        await WaitUntilAsync(() => tokens.Count >= 3);
        loop.Invalidate(Content);                         // 上限に達している：止めない番
        await Task.Delay(50);

        Assert.False(tokens[2].IsCancellationRequested);
        slow.SetResult();
        await loop.Completion;
    }

    [Fact]
    public async Task ワークスペース切替で走行中の描画を止め_前の要求を持ち越さない()
    {
        // 前のワークスペースのファイルを描いている最中に切り替わった。その描画を待つ理由はもう無く、
        // 言語サーバーは切替で落とされるので、待てば最長で期限いっぱい新しいワークスペースが描かれない。
        var gate = new TaskCompletionSource();
        CancellationToken first = default;
        var runs = 0;
        var loop = Loop(() => true, async (_, ct) =>
        {
            if (++runs > 1)
                return;
            first = ct;
            await gate.Task;
            ct.ThrowIfCancellationRequested();
        });

        loop.Invalidate(Content);
        loop.Restart();

        Assert.True(first.IsCancellationRequested);
        gate.SetResult();
        await loop.Completion;
        Assert.Equal(1, runs);                // 追従元が決まる前に、前の要求で描き直さない
        Assert.False(loop.HasPendingWork);
    }

    [Fact]
    public async Task ワークスペース切替のあとは追い越しの回数も数え直す()
    {
        // 前のワークスペースで上限まで追い越していると、切替後の最初の描画が
        // 「描き切らせる番」を引き継ぎ、新しいファイルへの切替で止められなくなる。
        var runs = 0;
        var tokens = new List<CancellationToken>();
        var loop = Loop(() => true, async (_, ct) =>
        {
            runs++;
            tokens.Add(ct);
            await Task.Delay(Timeout.Infinite, ct);
        });

        loop.Invalidate(Content);
        for (var i = 0; i < 2; i++)
        {
            loop.Invalidate(Content);
            await WaitUntilAsync(() => runs >= i + 2);
        }
        loop.Invalidate(Content);
        Assert.False(tokens[^1].IsCancellationRequested);   // 上限に達している：止めない番

        loop.Restart();
        await WaitUntilAsync(() => !loop.IsDraining);
        loop.Invalidate(Content);                           // 新しいワークスペースの描画
        await WaitUntilAsync(() => runs >= 4);
        loop.Invalidate(Content);                           // すぐ次のファイルへ

        Assert.True(tokens[3].IsCancellationRequested);
        loop.Restart();
    }

    /// <summary>条件が成り立つまで待つ（成り立たなければ失敗させるため、待ちきって戻る）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }

    /// <summary>本番と同じ形（見回り付き）で組み立てる。</summary>
    private static EditorSupportUpdateLoop Loop(
        Func<bool> canRender,
        Func<EditorSupportUpdateReason, CancellationToken, Task> render,
        Action<Exception>? onError = null,
        IEditorSupportRenderabilityWatch? watch = null)
        => new(canRender, render, watch ?? new FakeWatch(), onError);

    /// <summary>見回りの起床を手で進める（実装の DispatcherTimer と同じく一発きり）。</summary>
    private sealed class FakeWatch : IEditorSupportRenderabilityWatch
    {
        private Action? _tick;

        public List<TimeSpan> Delays { get; } = [];
        public bool IsArmed => _tick is not null;

        public void Schedule(TimeSpan delay, Action tick)
        {
            Delays.Add(delay);
            _tick = tick;
        }

        public void Cancel() => _tick = null;

        public void Fire()
        {
            var tick = _tick ?? throw new InvalidOperationException("見回りが仕掛かっていない");
            _tick = null;
            tick();
        }
    }
}
