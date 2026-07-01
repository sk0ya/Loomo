using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public class GitSubmoduleParserTests
{
    // git submodule status の1行は [状態フラグ(1桁)][ハッシュ(40桁)] path (describe) の形式。
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccc";
    private const string HashD = "dddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public void 正常な行は状態フラグなしとして読める()
    {
        var output = $" {HashA} libs/foo (heads/main)\n";

        var submodules = GitSubmoduleParser.Parse(output);

        var entry = Assert.Single(submodules);
        Assert.Equal("libs/foo", entry.Path);
        Assert.Equal(HashA, entry.Hash);
        Assert.Equal("heads/main", entry.Describe);
        Assert.False(entry.IsUninitialized);
        Assert.False(entry.HasDivergedCommit);
        Assert.False(entry.HasMergeConflict);
        Assert.Null(entry.StatusLabel);
    }

    [Fact]
    public void 先頭マイナスは未初期化として読める()
    {
        var output = $"-{HashB} libs/bar\n";

        var entry = Assert.Single(GitSubmoduleParser.Parse(output));

        Assert.Equal("libs/bar", entry.Path);
        Assert.Null(entry.Describe);
        Assert.True(entry.IsUninitialized);
        Assert.Equal("未初期化", entry.StatusLabel);
    }

    [Fact]
    public void 先頭プラスは登録コミットと異なるとして読める()
    {
        var output = $"+{HashC} libs/baz (v1.0-2-g{HashC[..7]})\n";

        var entry = Assert.Single(GitSubmoduleParser.Parse(output));

        Assert.Equal("libs/baz", entry.Path);
        Assert.True(entry.HasDivergedCommit);
        Assert.Equal("差分あり", entry.StatusLabel);
    }

    [Fact]
    public void 先頭Uはマージ未解決として読める()
    {
        var output = $"U{HashD} libs/qux\n";

        var entry = Assert.Single(GitSubmoduleParser.Parse(output));

        Assert.True(entry.HasMergeConflict);
        Assert.Equal("コンフリクト", entry.StatusLabel);
    }

    [Fact]
    public void サブモジュールが無ければ空リストになる()
    {
        Assert.Empty(GitSubmoduleParser.Parse(""));
        Assert.Empty(GitSubmoduleParser.Parse("\n"));
    }

    [Fact]
    public void 短縮ハッシュは先頭7文字になる()
    {
        var output = $" {HashA} libs/foo\n";

        var entry = Assert.Single(GitSubmoduleParser.Parse(output));

        Assert.Equal(HashA[..7], entry.ShortHash);
    }
}
