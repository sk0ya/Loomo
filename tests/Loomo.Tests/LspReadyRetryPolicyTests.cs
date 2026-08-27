using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 言語サーバーの準備待ちポーリングの判断。<b>コード表示が「接続待ち」から戻ってこられる唯一の目</b>で、
/// ここを閉じるとそのタブでは二度と構造も呼び出しパネルも出ない＝ペインが固まったようにしか見えない。
/// </summary>
public class LspReadyRetryPolicyTests
{
    private const int Max = 125;
    private const int Grace = 8;

    private static LspReadyRetryStep Next(bool codeSourceOpen, bool serverReady, int attempts)
        => LspReadyRetryPolicy.Next(codeSourceOpen, serverReady, attempts, Max, Grace);

    [Fact]
    public void 準備できたら描き直しを要求する()
        => Assert.Equal(LspReadyRetryStep.Render, Next(true, serverReady: true, attempts: 3));

    [Fact]
    public void 猶予の回にちょうど一度だけ案内のために描き直す()
    {
        // 毎 tick 投げると、待っている間じゅう画面を組み直し続けることになる。
        Assert.Equal(LspReadyRetryStep.Wait, Next(true, false, Grace - 1));
        Assert.Equal(LspReadyRetryStep.Render, Next(true, false, Grace));
        Assert.Equal(LspReadyRetryStep.Wait, Next(true, false, Grace + 1));
    }

    [Fact]
    public void コード表示でなくなったら止める()
        => Assert.Equal(LspReadyRetryStep.Stop, Next(codeSourceOpen: false, serverReady: false, attempts: 1));

    [Fact]
    public void 上限まで待ったら諦める()
    {
        Assert.Equal(LspReadyRetryStep.Wait, Next(true, false, Max));
        Assert.Equal(LspReadyRetryStep.Stop, Next(true, false, Max + 1));
    }

    /// <summary>
    /// サーバーが落ちて再接続待ちに戻ったとき、画面には落ちる前の構造が残っている（描画は案内の猶予中は
    /// あえて画面を触らない）。それを「もう用は済んだ」と読んで見張りを閉じると、サーバーが戻っても
    /// 誰も気づかず、構造も②パネルもそのタブの間ずっと古いままになる。
    /// </summary>
    [Fact]
    public void 画面に構造が残っていても見張りは止めない()
    {
        // 判断材料に「構造が出ているか」を持たせないことそのものが仕様。
        // 準備できていない限り、何回目であっても待ち続ける。
        Assert.Equal(LspReadyRetryStep.Wait, Next(true, serverReady: false, attempts: 1));
        Assert.Equal(LspReadyRetryStep.Wait, Next(true, serverReady: false, attempts: 50));
    }
}
