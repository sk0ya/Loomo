using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミットログの照会条件（git を起動しない部分）。ここが守っているのは
/// 「絞り込みは git へ渡る」「表示日時と git の日付基準のずれを窓で吸収する」「押し下げてはいけない
/// 条件（ハッシュ・ref）は渡さない」の3点。
/// </summary>
public class GitLogQueryTests
{
    private static string Args(GitLogQuery query) => string.Join(' ', query.ToArguments());

    [Fact]
    public void 既定はグラフ付きの全ブランチ()
    {
        var args = Args(new GitLogQuery { Limit = 50 });

        Assert.Contains("--graph", args);
        Assert.Contains("--all", args);
        Assert.Contains("-n50", args);
        Assert.DoesNotContain("--skip", args);
    }

    [Fact]
    public void パスは末尾の区切りより後ろへ置きグロブ解釈を止める()
    {
        var args = new GitLogQuery { PathFilter = "docs/a[1].md" }.ToArguments();

        Assert.Contains(GitCompareArgs.LiteralPathspecs, args);
        var separator = Array.IndexOf(args, "--");
        Assert.True(separator > 0);
        Assert.Equal("docs/a[1].md", args[separator + 1]);
    }

    [Fact]
    public void リネーム追跡はパスがあるときだけ付く()
    {
        Assert.Contains("--follow", Args(new GitLogQuery { PathFilter = "a.txt", FollowRenames = true }));
        // パスが無いのに --follow を渡すと git は fatal で終わる（履歴が丸ごと空になる）
        Assert.DoesNotContain("--follow", Args(new GitLogQuery { FollowRenames = true }));
    }

    [Fact]
    public void 作者と本文は固定文字列として渡す()
    {
        var args = Args(new GitLogQuery
        {
            Authors = new[] { "Alice" },
            Messages = new[] { "C++ 対応" },
        });

        Assert.Contains("--fixed-strings", args);
        Assert.Contains("--author=Alice", args);
        Assert.Contains("--grep=C++ 対応", args);
    }

    [Fact]
    public void 本文が複数のときだけ全一致にする()
    {
        // git の --grep 同士は既定で OR。AND にしたいので --all-match を足す
        Assert.Contains("--all-match", Args(new GitLogQuery { Messages = new[] { "fix", "log" } }));
        Assert.DoesNotContain("--all-match", Args(new GitLogQuery { Messages = new[] { "fix" } }));
    }

    [Fact]
    public void 日付の窓は前後1日広げて渡す()
    {
        var args = Args(new GitLogQuery
        {
            Since = new DateOnly(2026, 8, 10),
            Until = new DateOnly(2026, 8, 20),
        });

        // 表示しているのは作成日時、git が見るのはコミット日時で、rebase を通るとずれる。
        // 取りこぼすより広く引いて、最終判定はクライアント側の表示日時で行う。
        Assert.Contains("--since=2026-08-09 00:00:00", args);
        Assert.Contains("--until=2026-08-21 23:59:59", args);
    }

    [Fact]
    public void 絞り込みの有無を判定できる()
    {
        Assert.False(new GitLogQuery { BranchRef = "main", PathFilter = "a.txt" }.HasFilters);
        Assert.True(new GitLogQuery { Messages = new[] { "fix" } }.HasFilters);
    }

    // ===== 検索式からの押し下げ =====

    private static GitLogQuery Push(string filter) =>
        CommitLogFilter.Parse(filter).ApplyTo(new GitLogQuery());

    [Fact]
    public void 接頭辞付きの作者と本文は押し下げる()
    {
        var query = Push("author:Alice msg:修正");

        Assert.Equal(new[] { "Alice" }, query.Authors);
        Assert.Equal(new[] { "修正" }, query.Messages);
    }

    [Fact]
    public void 素の検索語は本文検索として押し下げる()
    {
        // git は作者と本文を AND で見るので「どちらでもいいから一致」は表現できない。
        // 実際の用途のほとんどが件名探しなので、本文側に寄せる。
        Assert.Equal(new[] { "リベース" }, Push("リベース").Messages);
    }

    [Fact]
    public void 十六進数に見える素の検索語は押し下げない()
    {
        // ハッシュ前方一致で1件を手繰る使い方を、本文検索に化けさせて「見つからない」にしないため
        var query = Push("1b1dcd9");

        Assert.Empty(query.Messages);
        Assert.Empty(query.Authors);
        Assert.False(query.HasFilters);
    }

    [Fact]
    public void 十六進数の文字だけの単語はハッシュ扱いしない()
    {
        // added / dead / feed / face … を押し下げないと、ふつうの語の検索が黙って
        // 「読み込み済みのページだけ」に戻る。数字を含むものだけハッシュとみなす。
        Assert.Equal(new[] { "added" }, Push("added").Messages);
        Assert.Equal(new[] { "deface" }, Push("deface").Messages);
        Assert.Empty(Push("1b1dcd9").Messages);
        Assert.Empty(Push("cafe1").Messages);
    }

    [Fact]
    public void ハッシュとrefは押し下げない()
    {
        var query = Push("hash:abc ref:main");

        Assert.False(query.HasFilters);
    }

    [Fact]
    public void 日付トークンは範囲へ均して押し下げる()
    {
        var month = Push("date:2026-07");
        Assert.Equal(new DateOnly(2026, 7, 1), month.Since);
        Assert.Equal(new DateOnly(2026, 7, 31), month.Until);

        // 「その日より後」でも下限はその日そのもの＝git 側を緩い篩に保つ
        // （クライアント側の文字列比較は date:>2026-08 で 2026-08-01 以降を通すので、
        //  ここで月末の翌日を渡すと git だけが8月を落として篩が逆転する）。
        var after = Push("date:>2026-01-01");
        Assert.Equal(new DateOnly(2026, 1, 1), after.Since);
        Assert.Null(after.Until);

        var afterMonth = Push("date:>2026-08");
        Assert.Equal(new DateOnly(2026, 8, 1), afterMonth.Since);

        var range = Push("date:2026-01-01..2026-03-31");
        Assert.Equal(new DateOnly(2026, 1, 1), range.Since);
        Assert.Equal(new DateOnly(2026, 3, 31), range.Until);
    }

    [Fact]
    public void 解釈できない日付は押し下げない()
    {
        // クライアント側の判定（前方一致）は残るので、git 側は素通しにする
        Assert.False(Push("date:きのう").HasFilters);
    }

    [Fact]
    public void 既にある条件と重ねると狭い方を採る()
    {
        var seed = new GitLogQuery
        {
            Since = new DateOnly(2026, 1, 1),
            Until = new DateOnly(2026, 12, 31),
            Authors = new[] { "Bob" },
        };

        var query = CommitLogFilter.Parse("date:2026-06 author:Alice").ApplyTo(seed);

        Assert.Equal(new DateOnly(2026, 6, 1), query.Since);
        Assert.Equal(new DateOnly(2026, 6, 30), query.Until);
        Assert.Equal(new[] { "Bob", "Alice" }, query.Authors);
    }
}

/// <summary>「リモート名＋ブランチ名」の分解。最初の "/" で切ると別のブランチを作ってしまう。</summary>
public class GitRemoteRefTests
{
    private static readonly string[] Remotes = { "origin", "team/fork", "up" };

    [Fact]
    public void 登録済みリモートの最長一致で切る()
    {
        Assert.Equal(("origin", "main"), GitRemoteRef.TrySplit("origin/main", Remotes));
        Assert.Equal(("origin", "feature/foo"), GitRemoteRef.TrySplit("origin/feature/foo", Remotes));
        Assert.Equal(("team/fork", "main"), GitRemoteRef.TrySplit("team/fork/main", Remotes));
    }

    [Fact]
    public void リモート名に一致しなければ分解しない()
    {
        // ローカル上流やローカルブランチ（"/" を含むだけ）をリモート追跡と取り違えない
        Assert.Null(GitRemoteRef.TrySplit("main", Remotes));
        Assert.Null(GitRemoteRef.TrySplit("feature/foo", Remotes));
        Assert.Null(GitRemoteRef.TrySplit("origin", Remotes));
        Assert.Null(GitRemoteRef.TrySplit("origin/", Remotes));
        Assert.Null(GitRemoteRef.TrySplit("", Remotes));
    }
}

/// <summary>クローン先フォルダー名の既定（git 自身と同じ規則）。</summary>
public class GitCloneNameTests
{
    [Theory]
    [InlineData("https://github.com/user/repo.git", "repo")]
    [InlineData("https://github.com/user/repo", "repo")]
    [InlineData("https://github.com/user/repo/", "repo")]
    [InlineData("git@github.com:user/repo.git", "repo")]
    [InlineData("C:\\src\\bare-repo.git", "bare-repo")]
    public void URLの末尾からフォルダー名を決める(string url, string expected) =>
        Assert.Equal(expected, GitCloneService.FolderNameFrom(url));
}
