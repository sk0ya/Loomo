using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ワークツリーまわりの純ロジック（git を起動しない）：<c>worktree list --porcelain</c> の読み取り、
/// 引数の組み立て、既定の置き場所、作成ダイアログの検証と要求の組み立て。
/// </summary>
public sealed class GitWorktreeLogicTests
{
    // ===== ブランチ元 =====

    [Theory]
    [InlineData("commit: wip\nbranch: Created from main\n", "main")]
    [InlineData("branch: Created from refs/remotes/origin/develop", "origin/develop")]
    [InlineData("branch: Created from refs/heads/release/1.2", "release/1.2")]
    [InlineData("branch: Created from HEAD", null)]                // どの枝だったか分からない
    [InlineData("commit: wip\nbranch: Created from main\ncommit (initial): x", null)]   // 最古が作成でない
    [InlineData("", null)]
    public void reflogの最古の行から作成元を拾う(string reflog, string? expected)
        => Assert.Equal(expected, GitWorktreeArgs.ParseCreatedFrom(reflog));

    [Fact]
    public void ブランチ元の設定キーは名前の点や斜線をそのまま副節にする()
        => Assert.Equal("branch.feature/v1.2.loomo-base", GitWorktreeArgs.OriginConfigKey("feature/v1.2"));

    // ===== porcelain の読み取り =====

    [Fact]
    public void porcelainはメイン_ブランチ_デタッチ_ロック_掃除対象を読み分ける()
    {
        const string output =
            "worktree C:/work/app\nHEAD 1111111111111111111111111111111111111111\nbranch refs/heads/main\n\n" +
            "worktree C:/work/app.worktrees/feature-x\nHEAD 2222222222222222222222222222222222222222\n" +
            "branch refs/heads/feature/x\nlocked 外付けドライブ\n\n" +
            "worktree C:/work/app.worktrees/det\nHEAD 3333333333333333333333333333333333333333\ndetached\n\n" +
            "worktree C:/gone\nHEAD 4444444444444444444444444444444444444444\nbranch refs/heads/old\n" +
            "prunable gitdir file points to non-existent location\n\n";

        var list = GitWorktreeParser.Parse(output);

        Assert.Equal(4, list.Count);
        Assert.True(list[0].IsMain);
        Assert.Equal(@"C:\work\app", list[0].Path);
        Assert.Equal("main", list[0].Branch);

        Assert.False(list[1].IsMain);
        Assert.Equal("feature/x", list[1].Branch);
        Assert.Equal("feature/x", list[1].DisplayName);
        Assert.True(list[1].IsLocked);
        Assert.Equal("外付けドライブ", list[1].LockReason);
        Assert.Equal("feature-x", list[1].FolderName);

        Assert.True(list[2].IsDetached);
        Assert.Null(list[2].Branch);
        Assert.Equal("(detached) 3333333", list[2].DisplayName);

        Assert.True(list[3].IsPrunable);
        Assert.False(list[3].Exists);
    }

    [Fact]
    public void porcelainは改行コードCRLFや末尾の空行なし_知らない行でも壊れない()
    {
        const string output =
            "worktree C:/work/app\r\nHEAD abc\r\nbranch refs/heads/main\r\nsomething-new value\r\n\r\n" +
            "worktree C:/work/bare\r\nbare";

        var list = GitWorktreeParser.Parse(output);

        Assert.Equal(2, list.Count);
        Assert.Equal("main", list[0].Branch);
        Assert.True(list[1].IsBare);
        Assert.False(list[1].CanRemove);
        Assert.False(list[0].CanRemove);   // メインは消せない
    }

    [Fact]
    public void 空の出力は空一覧()
    {
        Assert.Empty(GitWorktreeParser.Parse(""));
        Assert.Empty(GitWorktreeParser.Parse(null));
    }

    [Fact]
    public void 同じパスかはスラッシュと末尾区切りと大文字小文字を無視する()
    {
        var info = new GitWorktreeInfo { Path = @"C:\work\app" };
        Assert.True(info.IsSamePath("c:/work/app/"));
        Assert.False(info.IsSamePath(@"C:\work\app2"));
    }

    // ===== 引数 =====

    [Fact]
    public void 作成の引数は作り方ごとに組み立てる()
    {
        Assert.Equal(new[] { "worktree", "add", "-b", "feat", @"C:\w\feat", "main" },
            GitWorktreeArgs.AddArgs(new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, @"C:\w\feat", "feat", "main")));
        Assert.Equal(new[] { "worktree", "add", "-b", "feat", @"C:\w\feat" },
            GitWorktreeArgs.AddArgs(new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, @"C:\w\feat", "feat", null)));
        Assert.Equal(new[] { "worktree", "add", @"C:\w\x", "x" },
            GitWorktreeArgs.AddArgs(new GitWorktreeAddRequest(GitWorktreeAddMode.ExistingBranch, @"C:\w\x", "x", "ignored")));
        Assert.Equal(new[] { "worktree", "add", "--detach", @"C:\w\d", "HEAD" },
            GitWorktreeArgs.AddArgs(new GitWorktreeAddRequest(GitWorktreeAddMode.Detached, @"C:\w\d", null, null)));
    }

    [Fact]
    public void オプションに見える名前は引数にしない()
    {
        Assert.Throws<ArgumentException>(() => GitWorktreeArgs.AddArgs(
            new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, @"C:\w\x", "--force", null)));
        Assert.False(GitWorktreeArgs.IsValidReference("-b"));
        Assert.False(GitWorktreeArgs.IsValidReference("a b"));
        Assert.True(GitWorktreeArgs.IsValidReference("feature/x"));
    }

    [Fact]
    public void 削除とロックの引数()
    {
        Assert.Equal(new[] { "worktree", "remove", @"C:\w\x" }, GitWorktreeArgs.RemoveArgs(@"C:\w\x", force: false));
        Assert.Equal(new[] { "worktree", "remove", "--force", @"C:\w\x" }, GitWorktreeArgs.RemoveArgs(@"C:\w\x", force: true));
        Assert.Equal(new[] { "worktree", "lock", "--reason", "USB", @"C:\w\x" }, GitWorktreeArgs.LockArgs(@"C:\w\x", " USB "));
        Assert.Equal(new[] { "worktree", "lock", @"C:\w\x" }, GitWorktreeArgs.LockArgs(@"C:\w\x", " "));
    }

    [Fact]
    public void 削除の失敗が強制を要するものか見分ける()
    {
        Assert.True(GitWorktreeArgs.RemoveNeedsForce(
            "fatal: 'C:/w/x' contains modified or untracked files, use --force to delete it"));
        Assert.False(GitWorktreeArgs.RemoveNeedsForce("fatal: 'C:/w/x' is not a working tree"));
    }

    [Fact]
    public void 既定の置き場所はリポジトリの外の名前worktreesの下()
    {
        Assert.Equal(@"C:\work\app.worktrees\feature-login",
            GitWorktreeArgs.SuggestPath(@"C:\work\app", "feature/login"));
        Assert.Equal(@"C:\work\app.worktrees\worktree", GitWorktreeArgs.SuggestPath(@"C:\work\app\", null));
    }

    [Theory]
    [InlineData("feature/login", "feature-login")]
    [InlineData("fix:a*b?", "fix-a-b")]
    [InlineData("  name. ", "name")]
    [InlineData("a//b", "a-b")]
    public void ブランチ名はフォルダー名として使える形にする(string input, string expected)
        => Assert.Equal(expected, GitWorktreeArgs.SanitizeFolderName(input));

    // ===== 作成ダイアログ =====

    private static readonly GitWorktreeBranchContext Branches = new(
        Local: new[] { "main", "feature" },
        Remote: new[] { "origin/main", "origin/topic" },
        CheckedOut: new[] { "main" });

    private static string EmptyTarget() => Path.Combine(Path.GetTempPath(), "loomo-wt-policy", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 新しいブランチは既存名と空と不正名を弾く()
    {
        var path = EmptyTarget();
        Assert.NotNull(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.NewBranch, "", null, path, Branches));
        Assert.Contains("既にあります",
            GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.NewBranch, "feature", null, path, Branches));
        Assert.NotNull(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.NewBranch, "-x", null, path, Branches));
        Assert.Null(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.NewBranch, "new-one", "main", path, Branches));
    }

    [Fact]
    public void 既存ブランチはチェックアウト中のものを弾く()
    {
        var path = EmptyTarget();
        Assert.Contains("チェックアウト中",
            GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.ExistingBranch, "main", null, path, Branches));
        // origin/main はローカル main として置かれる → main はチェックアウト中なので同じく弾く。
        Assert.Contains("チェックアウト中",
            GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.ExistingBranch, "origin/main", null, path, Branches));
        Assert.Null(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.ExistingBranch, "feature", null, path, Branches));
        Assert.Null(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.ExistingBranch, "origin/topic", null, path, Branches));
        Assert.NotNull(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.ExistingBranch, "nope", null, path, Branches));
    }

    [Fact]
    public void 置き場所は空でないフォルダーを弾く()
    {
        var path = EmptyTarget();
        Directory.CreateDirectory(path);
        try
        {
            Assert.Null(GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.Detached, null, null, path, Branches));
            File.WriteAllText(Path.Combine(path, "x.txt"), "x");
            Assert.Contains("空ではありません",
                GitWorktreeCreatePolicy.Validate(GitWorktreeAddMode.Detached, null, null, path, Branches));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void リモートのブランチを既存として選ぶと追跡付きのローカルブランチを作る要求になる()
    {
        var request = GitWorktreeCreatePolicy.BuildRequest(
            GitWorktreeAddMode.ExistingBranch, "origin/topic", null, @"C:\w\topic", Branches);

        Assert.Equal(GitWorktreeAddMode.NewBranch, request.Mode);
        Assert.Equal("topic", request.Branch);
        Assert.Equal("origin/topic", request.StartPoint);
    }

    [Fact]
    public void 同名のローカルがあるリモートはローカルを置く()
    {
        var context = Branches with { CheckedOut = Array.Empty<string>() };
        var request = GitWorktreeCreatePolicy.BuildRequest(
            GitWorktreeAddMode.ExistingBranch, "origin/main", null, @"C:\w\main", context);

        Assert.Equal(GitWorktreeAddMode.ExistingBranch, request.Mode);
        Assert.Equal("main", request.Branch);
    }

    [Fact]
    public void 比較基準の差分要求はタブ名に相手を添える()
    {
        var target = new sk0ya.Loomo.App.ViewModels.DiffOpenTarget.CompareBase(
            new GitCompareBaseSelection(GitCompareBaseKind.Worktree, @"C:\w\feat"), "feat");
        Assert.Equal(" ⇄ feat", target.WindowTitle);
        Assert.Equal("a.cs ⇄ feat", target.TitleFor(@"C:\repo\src\a.cs"));
    }

    [Fact]
    public void 相対パスの置き場所はリポジトリを基準にする()
    {
        var request = GitWorktreeCreatePolicy.BuildRequest(
            GitWorktreeAddMode.NewBranch, "x", null, @"..\app-x", Branches, baseFolder: @"C:\work\app");
        Assert.Equal(@"C:\work\app-x", request.Path);
        Assert.Equal(@"D:\abs", GitWorktreeCreatePolicy.ResolvePath(@"D:\abs", @"C:\work\app"));
    }

    [Fact]
    public void 置き場所の自動追従は利用者が打ち直すまで()
    {
        Assert.True(GitWorktreeCreatePolicy.ShouldFollowBranch("", null));
        Assert.True(GitWorktreeCreatePolicy.ShouldFollowBranch(@"C:\a\b", @"c:\a\b"));
        Assert.False(GitWorktreeCreatePolicy.ShouldFollowBranch(@"D:\mine", @"C:\a\b"));
    }
}
