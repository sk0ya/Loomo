using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;

using Claim = sk0ya.Loomo.App.Services.WebViewDebugPort.Claim;

namespace sk0ya.Loomo.Tests;

/// <summary>WebView2 は UserDataFolder を共有する全コントロールが同一のブラウザ引数である必要があり、
/// それはプロセスをまたいでも同じ——Loomo を2つ起動したとき、片方が別のポートを選ぶと環境生成が
/// ERROR_INVALID_STATE (0x8007139F) で失敗し、負けた側のブラウザペインと EditorSupport が丸ごと無反応になる。
/// なので「誰か生きているインスタンスが居るなら、空きでなくてもその番号に合わせる」が満たすべき性質。</summary>
public class WebViewDebugPortTests
{
    private const int OwnPid = 1000;

    private static (int Port, IReadOnlyList<Claim> Claims) Resolve(
        IEnumerable<Claim> existing, Func<int, bool> alive, Func<int, bool> listening, int freePort)
        => WebViewDebugPort.Resolve(existing, alive, listening, () => freePort, OwnPid);

    [Fact]
    public void 控えが空なら空きポートを選んで自分を書き残す()
    {
        var (port, claims) = Resolve(Array.Empty<Claim>(), _ => false, _ => false, 9333);

        Assert.Equal(9333, port);
        Assert.Equal(new[] { new Claim(OwnPid, 9333) }, claims);
    }

    [Fact]
    public void 先行インスタンスのブラウザが握っているポートは空いていなくても引き継ぐ()
    {
        // 9333 は listen 中（＝共有ブラウザプロセスが居る）。素朴な空き探索なら 9334 を返す場面。
        var (port, _) = Resolve(new[] { new Claim(4321, 9333) }, _ => false, p => p == 9333, 9334);

        Assert.Equal(9333, port);
    }

    [Fact]
    public void まだlistenしていなくても控えの主が生きていれば引き継ぐ()
    {
        // 先行インスタンスの起動直後（WebView2 をまだ実体化していない）＝ポートは未 listen。
        var (port, _) = Resolve(new[] { new Claim(4321, 9333) }, pid => pid == 4321, _ => false, 9334);

        Assert.Equal(9333, port);
    }

    /// <summary>この修正の要点。控えを1行（持ち主 pid ＋番号）にしていた頃は、引き継いだ側が持ち主にならず、
    /// 元の持ち主が終了した瞬間に「誰も居ない」ように見えて、3つ目（＝2つ目の起動）が別の番号を選んでいた。</summary>
    [Fact]
    public void 引き継いだときも自分の申告を書き足すので次のインスタンスから見える()
    {
        var (port, claims) = Resolve(new[] { new Claim(4321, 9334) }, pid => pid == 4321, _ => false, 9333);
        Assert.Equal(9334, port);
        Assert.Contains(new Claim(OwnPid, 9334), claims);

        // 引き継いだ側（OwnPid）だけが生きている状態で次のインスタンスが起動しても、まだ listen していない
        // 9334 を選べる（空き探索なら 9333 を返してしまう場面）。
        var (next, _) = WebViewDebugPort.Resolve(claims, pid => pid == OwnPid, _ => false, () => 9333, 2000);
        Assert.Equal(9334, next);
    }

    [Fact]
    public void 誰も居ない申告は捨てて選び直す()
    {
        var (port, claims) = Resolve(new[] { new Claim(4321, 9999) }, _ => false, _ => false, 9333);

        Assert.Equal(9333, port);
        Assert.Equal(new[] { new Claim(OwnPid, 9333) }, claims);
    }

    [Fact]
    public void 前回の自分の申告は残骸として無視する()
    {
        // pid の使い回しで「自分が生きている」と誤判定しないこと。
        var (port, claims) = Resolve(new[] { new Claim(OwnPid, 9999) }, _ => true, _ => true, 9333);

        Assert.Equal(9333, port);
        Assert.Equal(new[] { new Claim(OwnPid, 9333) }, claims);
    }

    /// <summary>主が終了しても番号を握っているブラウザプロセスが残っていることがある（＝その番号に合わせるしかない）。
    /// ただし残す申告は1つだけ——無条件に残すと、番号が listen され続けるかぎり死んだ pid が溜まり続ける。</summary>
    [Fact]
    public void 主が死んでも番号を握っているなら合わせるが申告は重ねない()
    {
        var existing = new[] { new Claim(4321, 9334), new Claim(4322, 9334) };

        var (port, claims) = Resolve(existing, _ => false, p => p == 9334, 9333);

        Assert.Equal(9334, port);
        Assert.Equal(new[] { new Claim(4321, 9334), new Claim(OwnPid, 9334) }, claims);
    }

    [Fact]
    public void 生きた申告がある番号の居残りは落とす()
    {
        var existing = new[] { new Claim(4321, 9334), new Claim(4322, 9334) };

        var (_, claims) = Resolve(existing, pid => pid == 4322, p => p == 9334, 9333);

        Assert.Equal(new[] { new Claim(4322, 9334), new Claim(OwnPid, 9334) }, claims);
    }

    [Fact]
    public void 申告と実体がずれていたらlisten中の番号を採る()
    {
        var existing = new[] { new Claim(4321, 9335), new Claim(4322, 9333) };

        var (port, _) = Resolve(existing, _ => true, p => p == 9333, 9400);

        Assert.Equal(9333, port);
    }

    [Theory]
    [InlineData("4321 9333", 4321, 9333)]        // 旧形式（1行）もそのまま読める
    [InlineData("4321 9333\n4322 9333", 4321, 9333)]
    public void 控えを読む(string text, int pid, int port)
    {
        var claims = WebViewDebugPort.ParseClaims(text);

        Assert.Equal(pid, claims[0].ProcessId);
        Assert.Equal(port, claims[0].Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9333")]
    [InlineData("abc 9333")]
    [InlineData("4321 0")]
    [InlineData("4321 70000")]
    public void 壊れた行は無かったことにする(string content)
        => Assert.Empty(WebViewDebugPort.ParseClaims(content));

    [Fact]
    public void 壊れた行が混ざっていても生きている行は拾う()
    {
        var claims = WebViewDebugPort.ParseClaims("こわれた\n4321 9334\n\n4322 abc");

        Assert.Equal(new[] { new Claim(4321, 9334) }, claims);
    }

    [Fact]
    public void 書式は往復する()
    {
        var claims = new[] { new Claim(4321, 9333), new Claim(4322, 9333) };

        Assert.Equal(claims, WebViewDebugPort.ParseClaims(WebViewDebugPort.FormatClaims(claims)));
    }

    [Fact]
    public void 引き当て直しは控えに載っているlisten中の番号を優先する()
    {
        var claims = new[] { new Claim(4321, 9335) };

        // 探索帯には 9334 も listen しているが、Loomo が使ったことのある 9335 を採る。
        var port = WebViewDebugPort.SelectRunningPort(claims, new HashSet<int> { 9334, 9335 }, current: 9333);

        Assert.Equal(9335, port);
    }

    [Fact]
    public void 引き当て直しは控えに無くてもlisten中の番号を拾う()
        => Assert.Equal(9334, WebViewDebugPort.SelectRunningPort(
            Array.Empty<Claim>(), new HashSet<int> { 9334 }, current: 9333));

    [Fact]
    public void 引き当て直す先が無ければnull()
        => Assert.Null(WebViewDebugPort.SelectRunningPort(
            new[] { new Claim(4321, 9333) }, new HashSet<int> { 9333 }, current: 9333));
}
