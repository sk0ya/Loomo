using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミット一覧（GitSessionView の LogList）の幅の余りは先頭列「コミット」が吸い、日時・作成者・ID は
/// 固定幅のまま右端に張り付く（＝日時の右境界は詳細列との GridSplitter と重なり、一覧の右端の
/// 区切りは1本だけ）。幅の決め方が崩れると「余白が残ってつまみが2本に見える」か「はみ出して
/// 横スクロールバーが出る」のどちらかになる。
/// </summary>
public class GitLogColumnLayoutTests
{
    [Theory]
    [InlineData(380, 930, 660)]   // 余りが 270 ある（既定の3列より一覧が広い）
    [InlineData(650, 930, 900)]   // 中身がわずかに足りない
    public void 余りぶんだけ先頭列を伸ばして中身をビューポートに合わせる(
        double current, double viewport, double extent)
    {
        var fill = GitSessionView.LogFillColumnWidth(current, viewport, extent);

        // 先頭列を fill にすると中身の総幅はちょうどビューポート＝横スクロールバーが出ない
        Assert.Equal(viewport, extent - current + fill, 3);
    }

    [Fact]
    public void はみ出しているときは先頭列を詰める()
    {
        // 詳細列を広げて一覧が狭くなった（中身 900 に対しビューポート 800）
        var fill = GitSessionView.LogFillColumnWidth(380, 800, 900);

        Assert.Equal(280, fill, 3);
    }

    /// <summary>実寸へ詰める列（日時・作成者・ID）は、セルの余白を落としている（ColumnGapGridViewRowPresenter）
    /// ぶん、必ず隙間を上乗せした幅でなければ隣の列の文字と接する。日時は固定書式で全行が同じ幅なので、
    /// 上乗せを落とすと「2026-08-29 10:11shigekazukoya」が<b>全行</b>で出る。</summary>
    [Theory]
    [InlineData(96.4, 28.0)]   // 中身のほうが広い（日時）
    [InlineData(12.7, 20.0)]   // 見出しのほうが広い
    public void 途中の列には列間の隙間を上乗せする(double content, double header)
    {
        var width = GitSessionView.LogTailColumnWidth(content, header, isLastColumn: false);

        Assert.Equal(Math.Max(content, header) + ColumnGapGridViewRowPresenter.ColumnGap, width, 3);
        Assert.True(width - content >= ColumnGapGridViewRowPresenter.ColumnGap);
    }

    /// <summary>最後の列（ID）の右は隣の列ではなく縦スクロールバーと GridSplitter なので、
    /// ここで隙間を足すと ID の後ろだけ2つぶん空いて見える。</summary>
    [Fact]
    public void 最後の列には列間の隙間を上乗せしない()
    {
        var width = GitSessionView.LogTailColumnWidth(45.5, 12.7, isLastColumn: true);

        Assert.Equal(45.5, width, 3);
    }

    [Theory]
    [InlineData(380, 300, 640)]   // 作成者・日時だけで既にはみ出している
    [InlineData(120, 0, 640)]
    public void 詰めきれなくなったら下限で止めて横スクロールに任せる(
        double current, double viewport, double extent)
    {
        var fill = GitSessionView.LogFillColumnWidth(current, viewport, extent);

        Assert.Equal(120, fill);
    }
}
