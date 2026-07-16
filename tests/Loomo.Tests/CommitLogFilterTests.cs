using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public class CommitLogFilterTests
{
    private static GitLogRow Row(
        string hash = "abc1234def5678", string shortHash = "abc1234",
        string author = "koya", string date = "2026-07-16 14:30",
        string? refs = "HEAD -> main", string subject = "fix: バグ修正") =>
        new("*", hash, shortHash, author, date, refs, subject);

    [Fact]
    public void 空文字は全件通す()
    {
        var f = CommitLogFilter.Parse("   ");
        Assert.True(f.IsEmpty);
    }

    [Theory]
    [InlineData("バグ")]        // 件名
    [InlineData("koya")]        // 作者
    [InlineData("abc1234")]     // 短縮ハッシュ
    [InlineData("main")]        // ref
    public void 接頭辞なしは全項目に部分一致(string term)
    {
        Assert.True(CommitLogFilter.Parse(term).Matches(Row()));
    }

    [Fact]
    public void 接頭辞なしは大文字小文字を無視()
    {
        Assert.True(CommitLogFilter.Parse("KOYA").Matches(Row()));
    }

    [Theory]
    [InlineData("author:koya", true)]
    [InlineData("author:taro", false)]
    [InlineData("msg:修正", true)]
    [InlineData("msg:koya", false)]      // 作者名は件名フィルタにかからない
    [InlineData("hash:abc1234", true)]   // 短縮
    [InlineData("hash:def5678", true)]   // 完全ハッシュの一部
    [InlineData("ref:main", true)]
    [InlineData("ref:develop", false)]
    public void 接頭辞で対象フィールドを限定できる(string filter, bool expected)
    {
        Assert.Equal(expected, CommitLogFilter.Parse(filter).Matches(Row()));
    }

    [Fact]
    public void 複数トークンはANDで結合される()
    {
        Assert.True(CommitLogFilter.Parse("author:koya msg:fix").Matches(Row()));
        Assert.False(CommitLogFilter.Parse("author:koya msg:missing").Matches(Row()));
    }

    [Fact]
    public void 引用符で空白を含む値を指定できる()
    {
        var row = Row(subject: "add: 新しい 機能");
        Assert.True(CommitLogFilter.Parse("msg:\"新しい 機能\"").Matches(row));
        Assert.False(CommitLogFilter.Parse("msg:\"存在しない 語\"").Matches(row));
    }

    [Theory]
    // 対象行のコミット日は 2026-07-16
    [InlineData("date:>2026-01-01", true)]
    [InlineData("date:>2026-12-01", false)]
    [InlineData("date:<2026-12-01", true)]
    [InlineData("date:<2026-01-01", false)]
    [InlineData("date:>=2026-07-16", true)]
    [InlineData("date:<=2026-07-16", true)]
    [InlineData("date:2026-07", true)]                   // 前方一致（月）
    [InlineData("date:2026-08", false)]
    [InlineData("date:2026-01-01..2026-12-31", true)]    // 範囲内
    [InlineData("date:2026-01-01..2026-06-30", false)]   // 範囲外（上限より後）
    [InlineData("date:2026-08-01..", false)]             // 下限のみ・開始が未来 → 含まれない
    [InlineData("date:..2026-08-01", true)]              // 上限のみ・その日以前 → 含まれる
    public void 日付は比較演算子と範囲に対応する(string filter, bool expected)
    {
        Assert.Equal(expected, CommitLogFilter.Parse(filter).Matches(Row()));
    }

    [Fact]
    public void 日付範囲は両端を含む()
    {
        var row = Row(date: "2026-06-30 09:00");
        Assert.True(CommitLogFilter.Parse("date:2026-06-30..2026-06-30").Matches(row));
    }

    [Fact]
    public void 空値の接頭辞トークンは無視される()
    {
        // "author:" だけなら条件なし＝全件通す
        Assert.True(CommitLogFilter.Parse("author:").IsEmpty);
    }

    [Fact]
    public void 日付の無い行は日付トークンに合致しない()
    {
        var row = Row(date: "");
        Assert.False(CommitLogFilter.Parse("date:2026").Matches(row));
    }

    [Fact]
    public void DayOfは先頭10文字を返す()
    {
        Assert.Equal("2026-07-16", CommitLogFilter.DayOf(Row()));
        Assert.Null(CommitLogFilter.DayOf(Row(date: "short")));
    }
}
