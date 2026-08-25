using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>リネームを跨ぐ版のパス解決。<c>--follow</c> で並べた履歴には「いまの名前では
/// 存在しなかったコミット」が混じるので、その版のパスを持っていないと、開く・比べる・戻すが
/// 軒並み「このファイルはありません」で失敗する。</summary>
public class GitRenameTrailTests
{
    // git log --follow --format=%H --name-status -- src/new.cs の出力（新しい順）。
    private const string Output = """
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
        M	src/new.cs
        bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
        R100	src/old.cs	src/new.cs
        cccccccccccccccccccccccccccccccccccccccc
        M	src/old.cs
        """;

    [Fact]
    public void リネームより古い版は旧パスになる()
    {
        var trail = GitRenameTrail.Parse(Output, "src/new.cs");

        Assert.Equal("src/new.cs", trail["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);
        // リネームしたコミット自身は「新しい名前になった版」なので、まだ新パス。
        Assert.Equal("src/new.cs", trail["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]);
        Assert.Equal("src/old.cs", trail["cccccccccccccccccccccccccccccccccccccccc"]);
    }

    [Fact]
    public void リネームが無ければ全部いまのパス()
    {
        var trail = GitRenameTrail.Parse("""
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            M	src/new.cs
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
            A	src/new.cs
            """, "src/new.cs");

        Assert.All(trail.Values, path => Assert.Equal("src/new.cs", path));
    }

    [Fact]
    public void 追跡中のパス以外のリネームには反応しない()
    {
        var trail = GitRenameTrail.Parse("""
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            R100	docs/a.md	docs/b.md
            """, "src/new.cs");

        Assert.Equal("src/new.cs", trail["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);
    }

    [Fact]
    public void 出力が空なら表も空()
        => Assert.Empty(GitRenameTrail.Parse("", "src/new.cs"));
}
