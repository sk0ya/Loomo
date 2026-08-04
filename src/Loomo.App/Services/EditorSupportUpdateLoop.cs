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
/// 取りこぼしにはならない。</item>
/// </list>
///
/// <para>
/// すべて UI ディスパッチャ上で呼ばれる前提なのでロックは持たない（<c>await</c> の再開も
/// 同じディスパッチャへ戻る）。テストからは同期コンテキスト無しで直接叩ける。
/// </para>
/// </summary>
public sealed class EditorSupportUpdateLoop
{
    private readonly Func<bool> _canRender;
    private readonly Func<EditorSupportUpdateReason, CancellationToken, Task> _render;
    private readonly Action<Exception>? _onError;

    private EditorSupportUpdateReason _pending;
    private CancellationTokenSource? _running;
    private bool _draining;

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

    /// <summary>再描画を要求する。EditorSupport を更新する唯一の入口。</summary>
    public void Invalidate(EditorSupportUpdateReason reason = EditorSupportUpdateReason.Content)
    {
        if (reason == EditorSupportUpdateReason.None)
            return;

        _pending |= reason;

        if (_draining)
        {
            // 走行中の描画は既に古い前提で組み立てられている。止めて、いまの状態でやり直させる。
            _running?.Cancel();
            return;
        }

        Completion = DrainAsync();
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

                using var cts = new CancellationTokenSource();
                _running = cts;
                try
                {
                    await _render(reason, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 追い越された：下で要求を差し戻す。
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }
                finally
                {
                    _running = null;
                }

                // 中断された要求は達成されていないので次周回へ戻す（新しい要求とマージされる）。
                if (cts.IsCancellationRequested)
                    _pending |= reason;
            }
        }
        finally
        {
            _draining = false;
        }
    }
}
