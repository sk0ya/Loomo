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
/// 「描けるようになったか」を<b>あとで見に行く</b>ための起床。<see cref="EditorSupportUpdateLoop"/> は
/// 描けない間の要求を捨てずに残すが、それを<b>誰かが知らせに来る</b>のを当てにすると、
/// 知らせ忘れた経路の数だけ「中身が古いまま」が生まれる（舞台を降りる・俯瞰を閉じる・
/// 袖から戻す・切り離しから戻す…と可視化の経路は増え続ける）。
/// 要求が残っている間だけループ自身がこれで起き直すので、<b>知らせる側に義務が無い</b>。
/// </summary>
public interface IEditorSupportRenderabilityWatch
{
    /// <summary>次の見回りを仕掛ける（すでに仕掛けてあれば張り替える）。</summary>
    void Schedule(TimeSpan delay, Action tick);

    /// <summary>見回りを止める。</summary>
    void Cancel();
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
/// 残す</b>＝ dirty フラグ。以前は不可視なら単に return していたので、その更新は永久に失われていた。
/// <para>残した要求は<b>自分で拾い直す</b>——<see cref="IEditorSupportRenderabilityWatch"/> で
/// 描けるようになるまで見回る。要求を残すだけにして「可視化した側が <see cref="Invalidate"/> を
/// 呼ぶ」に頼っていたときは、呼び忘れた経路（俯瞰の開閉・舞台からタイルへ戻る…）の分だけ
/// 「ペインは見えているのに中身が古いまま」が残っていた。<b>知らせる側の義務にしない</b>のが
/// ここの要点で、明示の <see cref="Invalidate"/> は「即座に描く」ための早道でしかない。</para></item>
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

    /// <summary>
    /// 描けるようになったかを見回る間隔。<b>だんだん間を空ける</b>——可視化は普通すぐ起きるので
    /// 最初は細かく、ペインを閉じたまま何時間も置かれる場合は最後の間隔まで開く。
    /// 明示の <see cref="Invalidate"/> が来ればそちらが即座に描くので、これは取りこぼしの網でしかない。
    /// <para>
    /// <b>最後まで行っても止めない。</b>止めてしまうと、要求の回収がふたたび
    /// 「可視化した側が知らせに来る」前提に戻り、知らせ忘れた経路でだけ固まる——この網はそれを
    /// 無くすために置いてある。代わりに終端を十分長くして、閉じたままのペインの負担を
    /// 1分あたり2回の真偽判定まで落とす。
    /// </para>
    /// </summary>
    internal static readonly TimeSpan[] RenderabilityPollDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Func<bool> _canRender;
    private readonly Func<EditorSupportUpdateReason, CancellationToken, Task> _render;
    private readonly IEditorSupportRenderabilityWatch _watch;
    private readonly Action<Exception>? _onError;

    private EditorSupportUpdateReason _pending;
    private EditorSupportUpdateReason _runningReason;
    private CancellationTokenSource? _running;
    private bool _runningDeadlineArmed;
    /// <summary>最後に描き切ってから、戻ってこない描画を見限ったか（＝足場が壊れている証拠がある）。</summary>
    private bool _abandonedSinceCompletion;
    private bool _draining;
    private int _consecutiveCancellations;
    private int _pollStep;
    /// <summary><see cref="Restart"/> のたびに進む。走行中の描画がこれより前の世代なら、
    /// 中断・見限りのあとも要求を差し戻さない（前のワークスペースの要求を持ち越さない）。</summary>
    private int _generation;

    /// <param name="canRender">いま描いてよいか（ペインが実際に見えているか）。</param>
    /// <param name="render">1回分の描画。<paramref name="render"/> は渡された
    /// <see cref="CancellationToken"/> を尊重し、<b>UI への反映はキャンセル確認の直後に同期で</b>行うこと。</param>
    /// <param name="watch">描けない間、描けるようになったかを見に行くための起床。
    /// <b>省略可能にしていない</b>のがここの要点——省略できると、本番だけ網の無い状態で組み立てられる。</param>
    /// <param name="onError">描画が例外で落ちたときの通知（省略時は握りつぶす）。</param>
    public EditorSupportUpdateLoop(
        Func<bool> canRender,
        Func<EditorSupportUpdateReason, CancellationToken, Task> render,
        IEditorSupportRenderabilityWatch watch,
        Action<Exception>? onError = null)
    {
        _canRender = canRender;
        _render = render;
        _watch = watch;
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

    /// <summary>
    /// <b>もう古いのに止めずに待っている</b>描画に与える期限（テストから縮めるため internal）。
    /// 止めない特例は2つ——キャレットの要求は内容の描画を止めない、追い越しの上限に達したら描き切らせる——で、
    /// どちらも「その描画はいずれ返ってくる」を前提にしている。返ってこなければ待っている要求は永久に描かれず、
    /// 上限側では見限りも起きないのでループが走行中のまま閉じる。
    /// <para>
    /// <b>時間だけでは「戻ってこない」と決めない。</b>付けるのは2つの場合だけ。
    /// キャレットの要求が内容の描画を待っているとき——キャレット要求はコード表示でしか来ず、コード描画は
    /// 構造（8秒）＋②（prepare 8秒→incoming/outgoing 並行8秒）＋コールドの取り直し（8秒）で最悪およそ32秒に収まる。
    /// もう1つは、追い越しの上限に達した描画で、<b>最後に描き切ってから見限りが起きている</b>とき。
    /// 上限に達しただけで付けると、45秒を超える重い読み込み（巨大 CSV・Excel）が編集を続ける限り毎回打ち切られ、
    /// 永久に表示されない。見限りは「中断を伝えても返らなかった」実績なので、そこから先は時間で区切ってよい。
    /// 誰も待っていない描画（最新の要求を描いている）には付けない。
    /// </para>
    /// </summary>
    internal TimeSpan StaleRenderDeadline { get; set; } = TimeSpan.FromSeconds(45);

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
            else if (CaretWaitsOnContent(reason) || _abandonedSinceCompletion)
                ArmStaleDeadline();   // 止めない特例でも、戻ってこないまま待ち続けない（StaleRenderDeadline）
            return;
        }

        Completion = DrainAsync();
    }

    /// <summary>
    /// 表示の前提ごと入れ替わった（ワークスペース切替）。走行中の描画を止め、持ち越した要求・
    /// 追い越しの回数・見回りを捨てて、<b>何も要求されていない状態</b>からやり直す。
    /// <para>
    /// 捨てるのが要点。前のワークスペースの要求を差し戻すと、追従元が決まる前に描き直しが走るうえ、
    /// 走行中の描画を待てば言語サーバー（切替で落とされる）の期限いっぱい新しいワークスペースが描かれない。
    /// 追い越しの回数を持ち越すと、切替後の最初の描画が「描き切らせる番」を引き継いで止められなくなる。
    /// 新しい追従元は呼び元が <see cref="Invalidate"/> で知らせる。
    /// </para>
    /// </summary>
    public void Restart()
    {
        _generation++;
        _pending = EditorSupportUpdateReason.None;
        _consecutiveCancellations = 0;
        _abandonedSinceCompletion = false;
        _pollStep = 0;
        _watch.Cancel();
        _running?.Cancel();
    }

    /// <summary>キャレットの要求が、止めない決まりの内容描画を待っている。</summary>
    private bool CaretWaitsOnContent(EditorSupportUpdateReason reason)
        => reason == EditorSupportUpdateReason.Caret
           && _runningReason.HasFlag(EditorSupportUpdateReason.Content);

    /// <summary>止めずに待つと決めた描画へ、戻ってこないとき用の期限を1回だけ仕掛ける。</summary>
    private void ArmStaleDeadline()
    {
        if (_runningDeadlineArmed || _running is not { IsCancellationRequested: false } running)
            return;
        // 要求が来るたびに張り直すと、打鍵が続く間は期限が永久に来ない。
        _runningDeadlineArmed = true;
        running.CancelAfter(StaleRenderDeadline);
    }

    /// <summary>
    /// 可視状態が変わったかもしれないので、残っている要求を拾い直す。<b>要求が無ければ何もしない</b>ので、
    /// レイアウトの組み直しのような頻繁な合図から気軽に呼べる（<see cref="Invalidate"/> と違って
    /// 描き直しを起こさない）。見回りの起床もここへ合流する。
    /// </summary>
    public void PollRenderability()
    {
        if (_pending == EditorSupportUpdateReason.None)
        {
            _watch.Cancel();
            _pollStep = 0;
            return;
        }
        if (_draining)
            return;             // 走行中：描き終わったループが自分でもう一周する
        if (!_canRender())
        {
            ArmWatch(advance: true);   // 見回りが空振りした：次はもう少し間を空けて見に来る
            return;
        }
        Completion = DrainAsync();
    }

    /// <summary>
    /// 次の見回りを仕掛ける。間隔は<b>見回りが実際に空振りするたび</b>に空けていく。
    /// </summary>
    /// <param name="advance">見回りが1回鳴って、それでもまだ描けなかったか。
    /// <b>要求が積み直されただけ</b>（<see cref="DrainAsync"/> からの再武装）では段階を進めない——
    /// 進めると、ペインを閉じたまま4回編集した／タブを4回切り替えただけで終端の30秒へ飛び、
    /// 「可視化は普通すぐ起きるので最初は細かく」という刻みが<b>いちばん普通の場合にこそ</b>
    /// 使われなくなる（知らせ忘れた経路があったとき、古い内容が250msではなく30秒居座る）。</param>
    private void ArmWatch(bool advance)
    {
        if (advance && _pollStep < RenderabilityPollDelays.Length - 1)
            _pollStep++;
        _watch.Schedule(RenderabilityPollDelays[_pollStep], PollRenderability);
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
                // 描けないなら要求を残したまま抜ける＝ dirty。可視化を知らせてもらえなくても、
                // 見回り（ArmWatch）が描けるようになった時点で拾い直す。
                if (!_canRender())
                {
                    ArmWatch(advance: false);
                    return;
                }
                _watch.Cancel();        // 描ける：もう見回りは要らない
                _pollStep = 0;

                var reason = _pending;
                _pending = EditorSupportUpdateReason.None;
                _runningReason = reason;
                var generation = _generation;

                // 見限った描画はトークンを持ったまま生き続けるので、その場合は cts を捨てない
                // （破棄済みトークンでの再開は例外の出方が変わる）。
                var cts = new CancellationTokenSource();
                _running = cts;
                _runningDeadlineArmed = false;
                var render = InvokeRenderAsync(reason, cts.Token);
                var completed = render.IsCompleted || await AwaitOrAbandonAsync(render, cts);
                _running = null;

                if (generation != _generation)
                {
                    // 走っている間にやり直し（Restart）が入った：前の世代の要求は差し戻さず、回数も数えない。
                    if (completed)
                        cts.Dispose();
                    continue;
                }

                if (!completed)
                {
                    // 戻ってこない描画は見限る。要求は残すので、次周回で新しい描画がやり直す。
                    _pending |= reason;
                    _consecutiveCancellations++;
                    _abandonedSinceCompletion = true;
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
                    _abandonedSinceCompletion = false;
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
