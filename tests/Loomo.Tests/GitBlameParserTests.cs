using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public class GitBlameParserTests
{
    [Fact]
    public void 同一コミットが続く行でもメタ情報が毎行くり返されるものとして読める()
    {
        // --line-porcelain は --porcelain と違い、同じコミットの連続行でもメタ情報を省略しない。
        var output =
            "eae05fa22d120fd334738d6aed8d5746b60fad82 1 1 2\n" +
            "author shigekazukoya\n" +
            "author-mail <shigekazukoya@gmail.com>\n" +
            "author-time 1780225844\n" +
            "author-tz +0900\n" +
            "committer shigekazukoya\n" +
            "committer-mail <shigekazukoya@gmail.com>\n" +
            "committer-time 1780225844\n" +
            "committer-tz +0900\n" +
            "summary CLAUDE.md を作成\n" +
            "filename CLAUDE.md\n" +
            "\t# CLAUDE.md\n" +
            "eae05fa22d120fd334738d6aed8d5746b60fad82 2 2\n" +
            "author shigekazukoya\n" +
            "author-mail <shigekazukoya@gmail.com>\n" +
            "author-time 1780225844\n" +
            "author-tz +0900\n" +
            "committer shigekazukoya\n" +
            "committer-mail <shigekazukoya@gmail.com>\n" +
            "committer-time 1780225844\n" +
            "committer-tz +0900\n" +
            "summary CLAUDE.md を作成\n" +
            "filename CLAUDE.md\n" +
            "\t\n";

        var lines = GitBlameParser.Parse(output);

        Assert.Equal(2, lines.Count);
        Assert.Equal("eae05fa22d120fd334738d6aed8d5746b60fad82", lines[0].Hash);
        Assert.Equal("eae05fa", lines[0].ShortHash);
        Assert.Equal("shigekazukoya", lines[0].Author);
        Assert.Equal(1, lines[0].OriginalLineNumber);
        Assert.Equal(1, lines[0].FinalLineNumber);
        Assert.Equal("# CLAUDE.md", lines[0].Content);
        Assert.Equal("2026-05-31 20:10", lines[0].AuthorDate); // author-time 1780225844 (UTC) + tz+0900

        Assert.Equal(2, lines[1].FinalLineNumber);
        Assert.Equal("", lines[1].Content);
    }

    [Fact]
    public void 複数コミットが混在するファイルを行ごとに読み分けられる()
    {
        var output =
            "1111111111111111111111111111111111111111 1 1 1\n" +
            "author Alice\n" +
            "author-mail <alice@example.com>\n" +
            "author-time 1700000000\n" +
            "author-tz +0000\n" +
            "summary 初回コミット\n" +
            "filename src/foo.cs\n" +
            "\tclass Foo\n" +
            "2222222222222222222222222222222222222222 1 2 1\n" +
            "author Bob\n" +
            "author-mail <bob@example.com>\n" +
            "author-time 1710000000\n" +
            "author-tz -0500\n" +
            "summary メソッド追加\n" +
            "filename src/foo.cs\n" +
            "\t{\n";

        var lines = GitBlameParser.Parse(output);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Alice", lines[0].Author);
        Assert.Equal("class Foo", lines[0].Content);
        Assert.Equal("Bob", lines[1].Author);
        Assert.Equal("{", lines[1].Content);
        Assert.Equal("2222222", lines[1].ShortHash);
        // 元ファイルでの行番号(1)と現在の行番号(2)が別々に読める
        Assert.Equal(1, lines[1].OriginalLineNumber);
        Assert.Equal(2, lines[1].FinalLineNumber);
    }

    [Fact]
    public void タブを含む行内容も先頭のタブだけを外して読める()
    {
        var output =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1 1 1\n" +
            "author Carol\n" +
            "author-time 1700000000\n" +
            "author-tz +0900\n" +
            "summary インデント\n" +
            "filename src/bar.cs\n" +
            "\t\tif (x)\n";

        var lines = GitBlameParser.Parse(output);

        var line = Assert.Single(lines);
        Assert.Equal("\tif (x)", line.Content); // 先頭の区切りタブだけを外す。内容自身のタブは残る。
    }

    [Fact]
    public void 空文字列は空リストになる()
    {
        Assert.Empty(GitBlameParser.Parse(""));
    }

    [Fact]
    public void previousやboundaryなどの追加メタ行があっても無視して読める()
    {
        var output =
            "3333333333333333333333333333333333333333 5 5 1\n" +
            "author Dave\n" +
            "author-time 1700000000\n" +
            "author-tz +0900\n" +
            "committer Dave\n" +
            "committer-time 1700000000\n" +
            "committer-tz +0900\n" +
            "previous 4444444444444444444444444444444444444444 src/baz.cs\n" +
            "summary リネーム前からの継続\n" +
            "filename src/baz.cs\n" +
            "\treturn 1;\n";

        var lines = GitBlameParser.Parse(output);

        var line = Assert.Single(lines);
        Assert.Equal("Dave", line.Author);
        Assert.Equal("return 1;", line.Content);
        Assert.Equal(5, line.FinalLineNumber);
    }
}
