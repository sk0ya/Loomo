using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミット一覧の先頭列はグラフ・refs・件名を1セルに詰めているので、列幅に収まらない件名は切れる。
/// 全文を出すツールチップの中身（＝切れたものを読む唯一の経路）を固定する。
/// </summary>
public sealed class GitLogRowToolTipTests
{
    private static GitLogRow Row(string? subject, string? refs = null, string? hash = "abc123") =>
        new("*", hash, "abc123", "Loomo Test", "2026-09-19 10:00", refs, subject);

    [Fact]
    public void 件名をそのまま出す()
    {
        Assert.Equal("長い長いコミットの件名", Row("長い長いコミットの件名").ToolTipText);
    }

    [Fact]
    public void refsがあれば2行目に添える()
    {
        // refs も同じセルで切れる側なので、件名だけ出して終わりにしない。
        Assert.Equal("直近の修正\nHEAD -> main, origin/main",
            Row("直近の修正", "HEAD -> main, origin/main").ToolTipText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 出すものが無い行にはツールチップを出さない(string? subject)
    {
        // 枝の継続行（"| |" だけの行）。null を返すと WPF はツールチップ自体を出さない
        // ——空文字だと空の吹き出しが開く。
        Assert.Null(Row(subject, refs: "origin/main", hash: null).ToolTipText);
    }
}
