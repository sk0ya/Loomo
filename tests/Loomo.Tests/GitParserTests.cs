using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public class GitParserTests
{
    [Fact]
    public void ステータス_ブランチとaheadbehindを読める()
    {
        var output =
            "# branch.oid 4f0e1c2d\n" +
            "# branch.head main\n" +
            "# branch.upstream origin/main\n" +
            "# branch.ab +2 -1\n";

        var snap = GitStatusParser.Parse(output);

        Assert.True(snap.IsRepository);
        Assert.Equal("main", snap.Branch);
        Assert.Equal("origin/main", snap.Upstream);
        Assert.Equal(2, snap.Ahead);
        Assert.Equal(1, snap.Behind);
        Assert.Empty(snap.Staged);
        Assert.Empty(snap.Unstaged);
    }

    [Fact]
    public void ステータス_変更がXYでステージ済みと未ステージへ振り分けられる()
    {
        var output =
            "# branch.head main\n" +
            "1 M. N... 100644 100644 100644 aaa bbb src/staged.cs\n" +
            "1 .M N... 100644 100644 100644 aaa bbb src/unstaged.cs\n" +
            "1 MM N... 100644 100644 100644 aaa bbb src/both.cs\n" +
            "? newfile.txt\n";

        var snap = GitStatusParser.Parse(output);

        Assert.Equal(new[] { "src/staged.cs", "src/both.cs" },
            snap.Staged.Select(e => e.Path).ToArray());
        Assert.Equal(new[] { "src/unstaged.cs", "src/both.cs", "newfile.txt" },
            snap.Unstaged.Select(e => e.Path).ToArray());
        Assert.True(snap.Unstaged.Single(e => e.Path == "newfile.txt").IsUntracked);
    }

    [Fact]
    public void ステータス_リネームはタブ区切りで新旧パスを読める()
    {
        var output =
            "# branch.head main\n" +
            "2 R. N... 100644 100644 100644 aaa bbb R100 new name.cs\told name.cs\n";

        var snap = GitStatusParser.Parse(output);

        var entry = Assert.Single(snap.Staged);
        Assert.Equal("new name.cs", entry.Path);
        Assert.Equal("old name.cs", entry.OrigPath);
    }

    [Fact]
    public void ステータス_コンフリクトは未ステージ側にIsConflictedで現れる()
    {
        var output =
            "# branch.head main\n" +
            "u UU N... 100644 100644 100644 100644 a b c src/conflict.cs\n";

        var snap = GitStatusParser.Parse(output);

        Assert.Empty(snap.Staged);
        var entry = Assert.Single(snap.Unstaged);
        Assert.Equal("src/conflict.cs", entry.Path);
        Assert.True(entry.IsConflicted);
    }

    [Fact]
    public void ログ_コミット行と枝の継続行を区別する()
    {
        var us = '\x1f';
        var output =
            $"* {us}abc123full{us}abc123{us}koya{us}2026-06-11 09:00{us}HEAD -> main, origin/main{us}修正コミット\n" +
            "|\\\n" +
            $"| * {us}def456full{us}def456{us}koya{us}2026-06-10 18:00{us}{us}ブランチ側の変更\n";

        var rows = GitLogParser.Parse(output);

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].IsCommit);
        Assert.Equal("*", rows[0].Graph);
        Assert.Equal("abc123", rows[0].ShortHash);
        Assert.Equal("HEAD -> main, origin/main", rows[0].Refs);
        Assert.Equal("修正コミット", rows[0].Subject);

        Assert.False(rows[1].IsCommit);
        Assert.Equal("|\\", rows[1].Graph);

        Assert.True(rows[2].IsCommit);
        Assert.Equal("| *", rows[2].Graph);
        Assert.Null(rows[2].Refs);
    }
}
