namespace sk0ya.Loomo.App.Services;

/// <summary>言語サーバーの準備待ちポーリングで、この tick に何をするか。</summary>
public enum LspReadyRetryStep
{
    /// <summary>見張りを止める（待つ理由が無い／待っても来ない）。</summary>
    Stop,

    /// <summary>そのまま待つ（画面には触らない）。</summary>
    Wait,

    /// <summary>描き直しを要求する（準備できた／案内を出す猶予を過ぎた）。</summary>
    Render,
}

/// <summary>
/// 言語サーバーが準備できるのを待つポーリングの判断。<b>コード表示が「接続待ち」から戻ってこられる
/// 唯一の目</b>がこれ——LSP の準備完了を知らせるイベントは購読していないので、ここを止めると
/// そのタブでは二度と構造が出ない。だから止める条件は極力持たせず、純関数にして固定してある。
///
/// <para>
/// <b>「画面に構造が出ているか」で止めてはいけない。</b>サーバーが落ちて再接続待ちに戻ったとき、
/// 画面には落ちる前の構造が残っている（描画は案内の猶予中はあえて画面を触らない）。それを
/// 「もう用は済んだ」と読んで見張りを閉じると、サーバーが戻ってきても誰も気づかず、
/// <b>構造も呼び出しパネルもそのタブの間ずっと古いまま</b>になる。
/// 止めるのは「そもそもコード表示ではない」と「上限まで待った」の2つだけで、
/// 準備できた後に止めるのは描画側の仕事（<c>StopLspReadyRetry</c>）。
/// </para>
/// </summary>
public static class LspReadyRetryPolicy
{
    /// <param name="codeSourceOpen">追従先がコード表示のファイルか。</param>
    /// <param name="serverReady">そのファイルを担当するサーバーが応答できる状態か。</param>
    /// <param name="attempts">この tick を含めた経過回数。</param>
    /// <param name="maxAttempts">これを超えたら諦める（ペインを開き直せばやり直される）。</param>
    /// <param name="noticeGraceTicks">「接続待ち」の案内を出す回。<b>ちょうどこの回だけ</b>描き直す
    /// ——毎 tick 要求を投げると、待っている間じゅう画面を組み直し続けることになる。</param>
    public static LspReadyRetryStep Next(
        bool codeSourceOpen, bool serverReady, int attempts, int maxAttempts, int noticeGraceTicks)
    {
        if (!codeSourceOpen || attempts > maxAttempts)
            return LspReadyRetryStep.Stop;
        return serverReady || attempts == noticeGraceTicks
            ? LspReadyRetryStep.Render
            : LspReadyRetryStep.Wait;
    }
}
