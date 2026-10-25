using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.76 このブランチで入れたコミットの印と、右クリックの見出し。</summary>
public sealed class GitBranchOwnCommitsTests
{
    [Fact]
    public void Marks_each_hash_once()
    {
        var marks = GitBranchOwnCommits.Marks(["a1", "b2", "a1"]);
        Assert.Equal(2, marks.Count);
        Assert.Equal(GitBranchOwnCommits.Mark, marks["a1"]);
    }

    [Fact]
    public void Menu_header_says_how_many_and_from_where()
    {
        Assert.Null(GitBranchOwnCommits.MenuHeader(0, "origin/main"));
        Assert.Equal("このブランチで入れたコミットを選ぶ（3 件・origin/main から）", GitBranchOwnCommits.MenuHeader(3, "origin/main"));
    }
}
