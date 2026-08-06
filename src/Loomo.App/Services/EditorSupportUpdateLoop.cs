namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport ペインの再描画を要求した理由。</summary>
[Flags]
public enum EditorSupportUpdateReason
{
    None = 0,

    /// <summary>表示内容そのものを組み直す（ファイル切替・本文変更・可視化・テーマ／モード変更など）。</summary>
    Content = 1,

    /// <summary>キャレット移動だけ（コードアウトラインの②呼び出しパネル差し替えで足りる）。</summary>
    Caret = 2,
}

/// <summary>
/// EditorSupport の再描画を<b>一本のループへ集約</b>する。ペインの更新契機は
/// タブ切替・本文変更・保存・ペイン可視化・舞台切替・ピン／スライド切替・キャレット移動・
/// ナビゲーション復帰…と十数か所に散っているが、外から触れる入口は
/// <see cref="Invalidate"/> ただ一つになる。
///
/// <para>守る不変条件は3つ。</para>
/// <list type="number">
/// <item><b>同時に走る描画は高々1本。</b>走行中に来た要求は畳んで（<c>_pending</c>）完了後に必ず
/// もう一周する。以前は各所が <c>_ = UpdateEditorSupportAsync()</c> を投げっぱなしにして、
/// 連番カウンタで「負けた側は黙って return」していたため、<b>途中まで UI を書き換えた描画が
/// 捨てられて中途半端な表示のまま固まる</b>ことがあった。</item>
/// <item><b>要求は決して消えない。</b>描けない状態（ペインが閉じている等）なら<b>描かずに要求を
/// 残す</b>＝ dirty フラグ。可視化されて <see cref="Invalidate"/> が来た時点で必ず1回描かれる。
/// 以前は不可視なら単に return していたので、その更新は永久に失われていた。</item>
/// <item><b>新しい要求は古い描画を止める。</b>走行中に <see cref="Invalidate"/> が来たら実行中の
/// <see cref="CancellationToken"/> をキャンセルする。キャンセルされた要求は次周回へ差し戻すので、
/// 取りこぼしにはならない。ただし止めてよいのは<b>本当に古くなった描画だけ</b>で、
/// 止め続けて一度も描き終わらないのは「固まる」そのものなので、次の2つの歯止めを置く。
/// <list type="bullet">
/// <item>キャレットだけの要求は、内容を組み直している最中の描画を捨てさせない（そのまま次周回へ回す）。
/// 本文の組み立てはキャレット移動では古くならないうえ、②パネルは終わった直後の周回で差し替わる。</item>
/// <item>同じ描画を <see cref="MaxConsecutiveCancellations"/> 回追い越したら、次の1回は
/// <b>必ず描き切らせる</b>。要求は <c>_pending</c> に残るので直後にもう一周する。
/// 上限が無いと、LSP 応答（最長 8 秒×数本）より短い間隔で編集やタブ切替が続くだけで、
/// 描画が毎回やり直しになって<b>永久に画面が更新されない</b>。</item>
/// </list></item>
/// <item><b>戻ってこない描画に道連れにされない。</b>キャンセルを伝えたのに
/// <see cref="AbandonAfterCancel"/> を過ぎても <c>render</c> が返らないなら見限って次へ進む。
/// WebView2 の初期化や巨大ファイルの読み込みが返らないと、以前はループが走行中のまま閉じ、
/// 以後どの要求も<b>二度と描かれなかった</b>。</item>
/// </list>
///
/// <para>
/// すべて UI ディスパッチャ上で呼ばれる前提なのでロックは持たない（<c>await</c> の再開も
/// 同じディスパッチャへ戻る）。テストからは同期コンテキスト無しで直接叩ける。
/// </para>
/// </summary>
public sealed class EditorSupportUpdateLoop
{
    /// <summary>同じ描画を続けて追い越してよい回数。これを超えたら一度描き切らせる。</summary>
    private const int MaxConsecutiveCancellations = 2;

    private readonly Func<bool> _canRender;
    private readonly Func<EditorSupportUpdateReason, CancellationToken, Task> _render;
    private readonly Action<Exception>? _onError;

    private EditorSupportUpdateReason _pending;
    private EditorSupportUpdateReason _runningReason;
    private CancellationTokenSource? _running;
    private bool _draining;
    private int _consecutiveCancellations;

    /// <param name="canRender">いま描いてよいか（ペインが実際に見えているか）。</param>
    /// <param name="render">1回分の描画。<paramref name="render"/> は渡された
    /// <see cref="CancellationToken"/> を尊重し、<b>UI への反映はキャンセル確認の直後に同期で</b>行うこと。</param>
    /// <param name="onError">描画が例外で落ちたときの通知（省略時は握りつぶす）。</param>
    public EditorSupportUpdateLoop(
        Func<bool> canRender,
        Func<EditorSupportUpdateReason, CancellationToken, Task> render,
        Action<Exception>? onError = null)
    {
        _canRender = canRender;
        _render = render;
        _onError = onError;
    }

    /// <summary>未処理の要求が残っているか（＝可視化されたら描かれる）。テスト・診断用。</summary>
    public bool HasPendingWork => _pending != EditorSupportUpdateReason.None;

    /// <summary>いまループが回っているか。テスト・診断用。</summary>
    public bool IsDraining => _draining;

    /// <summary>直近に開始したドレイン（テストから待つため）。</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>キャンセルを伝えた描画を見限るまでの猶予（テストから縮めるため internal）。</summary>
    internal TimeSpan AbandonAfterCancel { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>再描画を要求する。EditorSupport を更新する唯一の入口。</summary>
    public void Invalidate(EditorSupportUpdateReason reason = EditorSupportUpdateReason.Content)
    {
        if (reason == EditorSupportUpdateReason.None)
            return;

        _pending |= reason;

        if (_draining)
        {
            // 走行中の描画が古くなったなら止めて、いまの状態でやり直させる。
            if (ShouldPreempt(reason))
                _running?.Cancel();
            return;
        }

        Completion = DrainAsync();
    }

    /// <summary>走行中の描画を、いま来た要求のために捨ててよいか。</summary>
    private bool ShouldPreempt(EditorSupportUpdateReason reason)
    {
        if (_running is null || _running.IsCancellationRequested)
            return false;   // 走っていない／もう止めてある
        // キャレット移動では組み立て中の内容は古くならない。②パネルは次周回で差し替わる。
        if (reason == EditorSupportUpdateReason.Caret
            && _runningReason.HasFlag(EditorSupportUpdateReason.Content))
            return false;
        // 追い越し続けて一度も描き終わらない＝固まる。上限を超えたら描き切らせる。
        return _consecutiveCancellations < MaxConsecutiveCancellations;
    }

    private async Task DrainAsync()
    {
        _draining = true;
        try
        {
            while (_pending != EditorSupportUpdateReason.None)
            {
                // 描けないなら要求を残したまま抜ける＝ dirty。次の Invalidate（可視化）で必ず描かれる。
                if (!_canRender())
                    return;

                var reason = _pending;
                _pending = EditorSupportUpdateReason.None;
                _runningReason = reason;

                // 見限った描画はトークンを持ったまま生き続けるので、その場合は cts を捨てない
                // （破棄済みトークンでの再開は例外の出方が変わる）。
                var cts = new CancellationTokenSource();
                _running = cts;
                var render = InvokeRenderAsync(reason, cts.Token);
                var completed = render.IsCompleted || await AwaitOrAbandonAsync(render, cts);
                _running = null;

                if (!completed)
                {
                    // 戻ってこない描画は見限る。要求は残すので、次周回で新しい描画がやり直す。
                    _pending |= reason;
                    _consecutiveCancellations++;
                    _onError?.Invoke(new TimeoutException(
                        $"EditorSupport の描画が中断要求に応答しないので打ち切りました（reason={reason}）。"));
                    continue;
                }

                cts.Dispose();

                // 中断された要求は達成されていないので次周回へ戻す（新しい要求とマージされる）。
                if (cts.IsCancellationRequested)
                {
                    _pending |= reason;
                    _consecutiveCancellations++;
                }
                else
                {
                    _consecutiveCancellations = 0;   // 一度描き切ったので、また追い越してよい
                }
            }
        }
        finally
        {
            _draining = false;
            _runningReason = EditorSupportUpdateReason.None;
        }
    }

    /// <summary>1回分の描画。例外はここで畳むので、呼び元は「返ってきたか」だけを見ればよい。</summary>
    private async Task InvokeRenderAsync(EditorSupportUpdateReason reason, CancellationToken ct)
    {
        try
        {
            await _render(reason, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 追い越された：呼び元が要求を差し戻す。
        }
        catch (Exception ex)
        {
            // 自分のトークン以外で中断された描画（初期化の打ち切り等）もここへ来る。以前は
            // OperationCanceledException を無条件に握りつぶしていたので、描かれなかったことが
            // 誰にも見えないまま古い表示が残った。
            _onError?.Invoke(ex);
        }
    }

    /// <summary>描画の完了を待つ。<b>正常な描画に期限は設けず</b>、キャンセルを伝えたのに
    /// 返ってこないときだけ猶予を切って見限る。戻り値は「返ってきたか」。</summary>
    private async Task<bool> AwaitOrAbandonAsync(Task render, CancellationTokenSource cts)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cts.Token.Register(() => cancelled.TrySetResult());
        if (await Task.WhenAny(render, cancelled.Task) == render)
            return true;
        return await Task.WhenAny(render, Task.Delay(AbandonAfterCancel)) == render;
    }
}
