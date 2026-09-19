using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペインのヘッダーは、ブランチ切替＋検索＋作者＋期間＋解除＋右の各ボタンで 900px 近く要る。
/// 入りきらない幅では DockPanel が右から黙って切るので、絞り込み群は下の帯へ移す。
/// 「いつ移すか」と「帯をいつ出すか」を固定する。
/// </summary>
public sealed class GitPaneHeaderLayoutTests
{
    [Theory]
    [InlineData(1400, false, false)]   // 広い：ヘッダーのまま
    [InlineData(700, false, true)]     // 狭い：帯へ移す
    [InlineData(400, true, true)]
    [InlineData(1400, true, false)]    // 広げ切れば戻る
    public void 幅で畳むかどうかが決まる(double width, bool wasCompact, bool expected)
    {
        Assert.Equal(expected, ShellWindow.IsCompactGitHeader(width, wasCompact));
    }

    [Fact]
    public void 実測の必要幅より狭い閾値にしない()
    {
        // 880px はヘッダーに並びきらない（≈900px 要る）幅。ここで畳まれないと、
        // 直そうとしている「右から黙って切れる」がそのまま残る。
        Assert.True(ShellWindow.IsCompactGitHeader(880, wasCompact: false));
    }

    [Fact]
    public void 境界の幅では今の状態を保つ()
    {
        // 単一の閾値だと、ちょうどその幅でドラッグを止めたときに移動と復帰を繰り返して震える。
        // 930px は「畳んでいるなら畳んだまま、開いているなら開いたまま」。
        Assert.True(ShellWindow.IsCompactGitHeader(930, wasCompact: true));
        Assert.False(ShellWindow.IsCompactGitHeader(930, wasCompact: false));
    }

    [Theory]
    [InlineData(false, true, true, true, false)]    // 広いときは帯を出さない（ヘッダーに本体が居る）
    [InlineData(true, true, false, true, true)]     // 畳んでいて、自分で開いた
    [InlineData(true, false, false, true, false)]   // 畳んでいて、閉じている
    [InlineData(true, false, true, true, true)]     // 閉じていても、絞り込みが効いているなら出す
    [InlineData(true, true, true, false, false)]    // リポジトリでなければ絞る対象が無い
    public void 帯を出す条件(bool compact, bool toggled, bool hasActiveFilters, bool isRepository, bool expected)
    {
        // 4件目が肝：一覧が絞られているのに、その理由が画面のどこにも無い状態を作らない。
        Assert.Equal(expected,
            ShellWindow.ShouldShowGitFilterBand(compact, toggled, hasActiveFilters, isRepository));
    }
}
