using System;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 比較基準の純粋なロジック（既定ブランチの推定・git 引数の組み立て・<c>--name-status</c> の解析・
/// 基準ごとに許される操作の判定）。git も UI も起動しない。
/// </summary>
public class GitCompareBaseLogicTests
{
    // ===== 既定ブランチの推定 =====

    [Fact]
    public void 既定ブランチはoriginのHEADを第一候補にする()
    {
        var picked = GitCompareArgs.PickDefaultBranch(
            "refs/remotes/origin/develop\n",
            new[] { "main", "master", "origin/develop", "origin/main" });

        Assert.Equal("origin/develop", picked);
    }

    [Fact]
    public void originのHEADが無ければmain_master_の順で実在するものを選ぶ()
    {
        Assert.Equal("main", GitCompareArgs.PickDefaultBranch(null, new[] { "feature", "main", "master" }));
        Assert.Equal("master", GitCompareArgs.PickDefaultBranch(null, new[] { "feature", "master" }));
        Assert.Equal("origin/main", GitCompareArgs.PickDefaultBranch(null, new[] { "feature", "origin/main" }));
        Assert.Equal("origin/master", GitCompareArgs.PickDefaultBranch(null, new[] { "origin/master" }));
    }

    [Fact]
    public void リモート追跡が無く候補も無ければnullで壊れない()
    {
        Assert.Null(GitCompareArgs.PickDefaultBranch(null, Array.Empty<string>()));
        Assert.Null(GitCompareArgs.PickDefaultBranch(null, new[] { "feature", "topic/x" }));
        Assert.Null(GitCompareArgs.PickDefaultBranch("", new[] { "feature" }));
    }

    [Fact]
    public void originのHEADが候補一覧に無ければ次の候補へ落ちる()
    {
        // fetch 前などで origin/HEAD の指す枝がまだローカルに無いケース。
        var picked = GitCompareArgs.PickDefaultBranch(
            "refs/remotes/origin/develop", new[] { "main" });

        Assert.Equal("main", picked);
    }

    [Theory]
    [InlineData("refs/remotes/origin/main", "origin/main")]
    [InlineData("  refs/remotes/upstream/trunk\n", "upstream/trunk")]
    [InlineData("refs/heads/main", null)]
    [InlineData("refs/remotes/origin/HEAD", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void リモートHEADの短縮(string? input, string? expected)
        => Assert.Equal(expected, GitCompareArgs.ShortenRemoteHead(input));

    // ===== git 引数の組み立て =====

    [Fact]
    public void 一覧の引数は二点記法でリネーム検出付き()
    {
        var args = GitCompareArgs.NameStatusArgs("origin/main");

        // 三点記法（origin/main...HEAD）は未コミットの変更を落とすので使わない。
        Assert.DoesNotContain(args, a => a.Contains("..."));
        // 末尾の "--" が無いと、ブランチ名と同名のディレクトリがあるだけで git が曖昧として弾き、
        // 差分があるのに一覧が空になる。
        Assert.Equal(new[]
        {
            "--no-optional-locks", "--literal-pathspecs", "diff", "--name-status", "--find-renames",
            "origin/main", "--",
        }, args);
    }

    [Fact]
    public void ファイル差分の引数はコンテキスト行数と新旧両方のパスを渡す()
    {
        var renamed = new GitCommitFileChange('R', "src/新しい 名前.cs", "src/古い 名前.cs");

        var args = GitCompareArgs.FileDiffArgs("abc1234", renamed, 5);

        Assert.Equal(new[]
        {
            "--no-optional-locks", "--literal-pathspecs", "diff", "--unified=5", "--find-renames",
            "abc1234", "--", "src/古い 名前.cs", "src/新しい 名前.cs",
        }, args);
    }

    [Fact]
    public void リネームでないファイルは新パスだけを渡す()
    {
        var args = GitCompareArgs.FileDiffArgs("main", new GitCommitFileChange('M', "a.txt", null), 3);

        Assert.Equal(new[]
        {
            "--no-optional-locks", "--literal-pathspecs", "diff", "--unified=3", "--find-renames",
            "main", "--", "a.txt",
        }, args);
    }

    [Fact]
    public void pathspecはリテラル指定でグロブを効かせない()
    {
        // git の pathspec は既定でワイルドカードが効くので、これが無いと "a[1].txt" の差分に
        // "a1.txt" が混ざる。一覧・差分の両方に付いていること。
        Assert.Contains(GitCompareArgs.LiteralPathspecs, GitCompareArgs.NameStatusArgs("main"));
        Assert.Contains(
            GitCompareArgs.LiteralPathspecs,
            GitCompareArgs.FileDiffArgs("main", new GitCommitFileChange('M', "a[1].txt", null), 3));
    }

    [Fact]
    public void 分岐点と存在確認の引数()
    {
        Assert.Equal(new[] { "merge-base", "main", "HEAD" }, GitCompareArgs.MergeBaseArgs("main"));
        Assert.Equal(new[] { "rev-parse", "--verify", "--quiet", "main^{commit}" },
            GitCompareArgs.VerifyCommitArgs("main"));
    }

    // ===== --name-status の解析 =====

    [Fact]
    public void nameStatusは追加削除変更リネームを読み分ける()
    {
        var output = string.Join("\n", new[]
        {
            "A\tadded.txt",
            "D\tdeleted.txt",
            "M\tsrc/changed.cs",
            "R100\told/name.cs\tnew/name.cs",
            "C075\tsrc/a.cs\tsrc/b.cs",
        });

        var changes = GitNameStatusParser.Parse(output);

        Assert.Equal(5, changes.Count);
        Assert.Equal(new GitCommitFileChange('A', "added.txt", null), changes[0]);
        Assert.Equal(new GitCommitFileChange('D', "deleted.txt", null), changes[1]);
        Assert.Equal(new GitCommitFileChange('M', "src/changed.cs", null), changes[2]);
        // リネームは削除＋追加の2件ではなく、旧パスを持つ1件にまとまる。
        Assert.Equal(new GitCommitFileChange('R', "new/name.cs", "old/name.cs"), changes[3]);
        Assert.Equal(new GitCommitFileChange('C', "src/b.cs", "src/a.cs"), changes[4]);
    }

    [Fact]
    public void nameStatusは空白や日本語を含むパスをそのまま読む()
    {
        var output = "M\tdocs/設計 書 メモ.md\nR090\tdocs/旧 名前.md\tdocs/新しい 名前.md\n";

        var changes = GitNameStatusParser.Parse(output);

        Assert.Equal(2, changes.Count);
        Assert.Equal("docs/設計 書 メモ.md", changes[0].Path);
        Assert.Equal("docs/新しい 名前.md", changes[1].Path);
        Assert.Equal("docs/旧 名前.md", changes[1].OrigPath);
    }

    [Fact]
    public void nameStatusは空行や壊れた行を捨てる()
    {
        var output = "\n\nM\ta.txt\r\nこれは区切りが無い行\n\tパスだけ\nD\t\n";

        var changes = GitNameStatusParser.Parse(output);

        Assert.Equal(new[] { new GitCommitFileChange('M', "a.txt", null) }, changes);
        Assert.Empty(GitNameStatusParser.Parse(""));
    }

    // ===== 基準ごとに許される操作 =====

    [Fact]
    public void 作業ツリー基準ではステージも破棄も行単位の適用もできる()
    {
        var caps = GitCompareCapabilities.For(GitCompareBaseKind.WorkingTree);

        Assert.True(caps.CanStage);
        Assert.True(caps.CanUnstage);
        Assert.True(caps.CanDiscard);
        Assert.True(caps.CanApplyLines);
        Assert.True(caps.CanCommit);
    }

    [Fact]
    public void ブランチや分岐点の基準ではインデックス概念の操作を一切出さない()
    {
        foreach (var kind in new[] { GitCompareBaseKind.Branch, GitCompareBaseKind.MergeBase })
        {
            var caps = GitCompareCapabilities.For(kind);
            Assert.False(caps.CanStage, kind.ToString());
            Assert.False(caps.CanUnstage, kind.ToString());
            Assert.False(caps.CanDiscard, kind.ToString());
            Assert.False(caps.CanApplyLines, kind.ToString());
            Assert.False(caps.CanCommit, kind.ToString());
        }
    }

    [Fact]
    public void 選択の性質()
    {
        Assert.True(GitCompareBaseSelection.WorkingTree.IsWorkingTree);
        Assert.False(GitCompareBaseSelection.WorkingTree.NeedsBranch);

        var branch = new GitCompareBaseSelection(GitCompareBaseKind.Branch, "main");
        Assert.False(branch.IsWorkingTree);
        Assert.True(branch.NeedsBranch);
        Assert.True(new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main").NeedsBranch);

        Assert.Equal(GitCompareCapabilities.For(branch), GitCompareCapabilities.For(GitCompareBaseKind.Branch));
    }
}
