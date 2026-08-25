using System.Diagnostics;
using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実際の git に対して、リモート操作（追加・上流・強制プッシュ・リモートブランチ削除・クローン）と
/// 特定リビジョンのファイル、絞り込み付きログを確かめる。
/// リモートは<b>ローカルのベアリポジトリ</b>を使う（ネットワークも認証も要らない）。
/// </summary>
[Collection(GitProcessTests.Name)]
public sealed class GitRemoteAndRevisionTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "loomo-git-remote-tests", Guid.NewGuid().ToString("N"));
    private string _root = "";
    private string _remote = "";
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(_base, "work");
        _remote = Path.Combine(_base, "remote.git");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_remote);

        await RunAsync(_remote, "init", "--bare");
        // ベアリポジトリの HEAD は既定で master を指す。こちらは main を押すので、
        // 揃えておかないと clone した側が「空の作業ツリー」になり、テストが黙って素通りする。
        await RunAsync(_remote, "symbolic-ref", "HEAD", "refs/heads/main");

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init", "-b", "main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_base, recursive: true); }
        catch { /* git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    // ===== リモートの管理 =====

    [Fact]
    public async Task リモートは追加とURL変更と削除ができる()
    {
        Assert.True((await _git.AddRemoteAsync("origin", _remote)).Success);
        Assert.Equal(new[] { "origin" }, (await _git.GetRemoteUrlsAsync()).Select(r => r.Name));

        var other = Path.Combine(_base, "other.git");
        Directory.CreateDirectory(other);
        await RunAsync(other, "init", "--bare");
        Assert.True((await _git.SetRemoteUrlAsync("origin", other)).Success);
        Assert.Equal(other, (await _git.GetRemoteUrlsAsync()).Single().Url);

        Assert.True((await _git.RemoveRemoteAsync("origin")).Success);
        Assert.Empty(await _git.GetRemoteUrlsAsync());
    }

    [Fact]
    public async Task 上流は設定と解除ができる()
    {
        await CommitAsync("a.txt", "a", "first");
        await _git.AddRemoteAsync("origin", _remote);
        await MustRunAsync("push", "origin", "main");

        Assert.True((await _git.SetUpstreamAsync("main", "origin/main")).Success);
        Assert.Equal("origin/main", await CurrentUpstreamAsync());

        Assert.True((await _git.UnsetUpstreamAsync("main")).Success);
        Assert.Null(await CurrentUpstreamAsync());
    }

    [Fact]
    public async Task リモートブランチの削除はリモート側から消す()
    {
        await CommitAsync("a.txt", "a", "first");
        await _git.AddRemoteAsync("origin", _remote);
        await MustRunAsync("push", "origin", "main");
        await MustRunAsync("push", "origin", "main:refs/heads/feature");
        await MustRunAsync("fetch", "origin");

        var result = await _git.DeleteRemoteBranchAsync("origin/feature");

        Assert.True(result.Success, result.Message);
        var refs = (await MustRunAsync("ls-remote", "--heads", "origin")).Output;
        Assert.DoesNotContain("refs/heads/feature", refs);
        Assert.Contains("refs/heads/main", refs);
    }

    [Fact]
    public async Task リモート名を特定できないブランチは削除しない()
    {
        await CommitAsync("a.txt", "a", "first");

        var result = await _git.DeleteRemoteBranchAsync("origin/feature");

        Assert.False(result.Success);
        Assert.Contains("リモートを特定できません", result.Message);
    }

    // ===== 強制プッシュ =====

    [Fact]
    public async Task 履歴を書き換えた後は通常のプッシュが拒否され強制プッシュが通る()
    {
        await CommitAsync("a.txt", "a", "first");
        await _git.AddRemoteAsync("origin", _remote);
        await MustRunAsync("push", "-u", "origin", "main");

        // amend＝リモートにあるコミットの置き換え。素の push は non-fast-forward で拒否される
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "--amend", "-m", "first (amended)");

        var normal = await _git.PushAsync();
        Assert.False(normal.Success);

        var forced = await _git.PushAsync(force: true);
        Assert.True(forced.Success, forced.Message);

        var remoteSubject = (await RunAsync(_remote, "log", "-1", "--format=%s", "main")).Output.Trim();
        Assert.Equal("first (amended)", remoteSubject);
    }

    [Fact]
    public async Task 強制プッシュはリモートが先に進んでいたら中止する()
    {
        await CommitAsync("a.txt", "a", "first");
        await _git.AddRemoteAsync("origin", _remote);
        await MustRunAsync("push", "-u", "origin", "main");

        // 他人が積んだコミットを模す（こちらは fetch していない＝lease の想定位置は古いまま）
        var other = await CloneForOtherPersonAsync();
        await File.WriteAllTextAsync(Path.Combine(other, "b.txt"), "b");
        await MustRunInAsync(other, "add", "-A");
        await MustRunInAsync(other, "commit", "-m", "他人のコミット");
        await MustRunInAsync(other, "push", "origin", "main");

        await MustRunAsync("commit", "--amend", "-m", "first (amended)");
        var forced = await _git.PushAsync(force: true);

        Assert.False(forced.Success);
        var remoteSubject = (await RunAsync(_remote, "log", "-1", "--format=%s", "main")).Output.Trim();
        Assert.Equal("他人のコミット", remoteSubject);
    }

    // ===== プルの方式 =====

    [Fact]
    public async Task 早送りのみのプルは分岐していると失敗する()
    {
        await CommitAsync("a.txt", "a", "first");
        await _git.AddRemoteAsync("origin", _remote);
        await MustRunAsync("push", "-u", "origin", "main");

        var other = await CloneForOtherPersonAsync();
        await File.WriteAllTextAsync(Path.Combine(other, "b.txt"), "b");
        await MustRunInAsync(other, "add", "-A");
        await MustRunInAsync(other, "commit", "-m", "リモート側");
        await MustRunInAsync(other, "push", "origin", "main");

        await CommitAsync("c.txt", "c", "ローカル側");   // 分岐させる

        var ffOnly = await _git.PullAsync(GitPullMode.FastForwardOnly);
        Assert.False(ffOnly.Success);

        var rebase = await _git.PullAsync(GitPullMode.Rebase);
        Assert.True(rebase.Success, rebase.Message);
        // リベースなのでマージコミットは作られない（親が1つだけ）
        var parents = (await MustRunAsync("log", "-1", "--format=%P")).Output.Trim();
        Assert.DoesNotContain(" ", parents);
    }

    // ===== 特定リビジョンのファイル =====

    [Fact]
    public async Task コミット時点の内容を取り出して作業ツリーへ戻せる()
    {
        var first = await CommitAsync("a.txt", "むかしの内容", "first");
        await CommitAsync("a.txt", "いまの内容", "second");

        var old = await _git.GetFileAtRevisionAsync(first, "a.txt");
        Assert.True(old.Success, old.Message);
        Assert.Equal("むかしの内容", old.Output.TrimEnd('\n', '\r'));

        var restored = await _git.RestoreFileAtRevisionAsync(first, "a.txt");
        Assert.True(restored.Success, restored.Message);
        Assert.Equal("むかしの内容",
            (await File.ReadAllTextAsync(Path.Combine(_root, "a.txt"))).TrimEnd('\n', '\r'));
    }

    [Fact]
    public async Task そのコミットに無いファイルは理由を返す()
    {
        var first = await CommitAsync("a.txt", "a", "first");
        await CommitAsync("b.txt", "b", "second");

        var missing = await _git.GetFileAtRevisionAsync(first, "b.txt");

        // 「空ファイル」と偽らず、無かったことを伝える
        Assert.False(missing.Success);
        Assert.Contains("b.txt", missing.Message);
    }

    // ===== 絞り込み付きログ =====

    [Fact]
    public async Task 作者と本文の絞り込みはgit側で効く()
    {
        await CommitAsync("a.txt", "a", "alpha を修正");
        await MustRunAsync("config", "user.name", "Другой");
        await CommitAsync("b.txt", "b", "beta を追加");
        await CommitAsync("c.txt", "c", "gamma を修正");

        var byAuthor = await _git.GetLogAsync(new GitLogQuery { Authors = new[] { "Loomo Test" } });
        Assert.Equal(new[] { "alpha を修正" }, Subjects(byAuthor));

        var byMessage = await _git.GetLogAsync(new GitLogQuery { Messages = new[] { "を修正" } });
        Assert.Equal(new[] { "gamma を修正", "alpha を修正" }, Subjects(byMessage));
    }

    [Fact]
    public async Task 検索式の押し下げは読み込み済みページの外にも届く()
    {
        // 1ページ(2件)より奥にある古いコミットを、ページングせずに1発で引き当てる
        await CommitAsync("old.txt", "old", "さがしものはこれ");
        for (var index = 0; index < 5; index++)
            await CommitAsync($"n{index}.txt", "n", $"ふつうのコミット {index}");

        var page = new GitLogQuery { Limit = 2 };
        Assert.DoesNotContain("さがしものはこれ", Subjects(await _git.GetLogAsync(page)));

        var filtered = CommitLogFilter.Parse("さがしもの").ApplyTo(page);
        Assert.Equal(new[] { "さがしものはこれ" }, Subjects(await _git.GetLogAsync(filtered)));
    }

    [Fact]
    public async Task リネームを追ってファイル履歴を続ける()
    {
        await CommitAsync("old.txt", "内容", "作成");
        await MustRunAsync("mv", "old.txt", "new.txt");
        await MustRunAsync("commit", "-m", "改名");

        var withFollow = await _git.GetLogAsync(
            new GitLogQuery { PathFilter = "new.txt", FollowRenames = true });
        var withoutFollow = await _git.GetLogAsync(new GitLogQuery { PathFilter = "new.txt" });

        Assert.Equal(new[] { "改名", "作成" }, Subjects(withFollow));
        Assert.Equal(new[] { "改名" }, Subjects(withoutFollow));
    }

    [Fact]
    public async Task リネーム前の版もその時点の名前で引けて今の名前へ戻せる()
    {
        // --follow で並べた履歴には「いまの名前では存在しなかったコミット」が混じる。
        // 名前を解決せずに git show <hash>:<いまのパス> を投げると、追跡で拾えるようにした行が
        // そっくり「このファイルはありません」で操作できない行になる。
        var before = await CommitAsync("old.txt", "むかしの内容", "作成");
        await MustRunAsync("mv", "old.txt", "new.txt");
        await MustRunAsync("commit", "-m", "改名");
        await CommitAsync("new.txt", "いまの内容", "更新");

        var trail = await _git.GetRenameTrailAsync("new.txt");
        Assert.Equal("old.txt", trail[before]);

        var old = await _git.GetFileAtRevisionAsync(before, trail[before]);
        Assert.True(old.Success, old.Message);
        Assert.Equal("むかしの内容", old.Output.Trim());

        // 戻し先はいまの名前。昔の名前のファイルが生えてくるのは「戻した」ではない。
        var restored = await _git.RestoreFileAtRevisionAsync(before, trail[before], renamedTo: "new.txt");
        Assert.True(restored.Success, restored.Message);
        Assert.Equal("むかしの内容",
            (await File.ReadAllTextAsync(Path.Combine(_root, "new.txt"))).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "old.txt")), "昔の名前のファイルが残っている");

        // インデックスにも昔の名前が残らない（残ると「昔の名前の削除」まで一緒にコミットされる）。
        var status = await _git.GetStatusAsync();
        Assert.DoesNotContain(status.Staged.Concat(status.Unstaged),
            change => change.Path.Contains("old.txt"));
    }

    // ===== クローン =====

    [Fact]
    public async Task クローンはワークスペースの対象を変えずに取得する()
    {
        await CommitAsync("a.txt", "a", "first");
        await _git.AddRemoteAsync("origin", _remote);
        await MustRunAsync("push", "origin", "main");

        var destination = Path.Combine(_base, "clones");
        Directory.CreateDirectory(destination);

        var result = await _git.CloneAsync(_remote, destination);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.Combine(destination, "remote"), result.TargetPath);
        Assert.True(File.Exists(Path.Combine(result.TargetPath, "a.txt")));
        // 現在の Git 対象は元のワークスペースフォルダーのまま
        Assert.Equal(_root, _git.RootPath);
    }

    [Fact]
    public async Task 既にあるフォルダーへはクローンしない()
    {
        var destination = Path.Combine(_base, "clones2");
        Directory.CreateDirectory(Path.Combine(destination, "taken"));

        var result = await _git.CloneAsync(_remote, destination, "taken");

        Assert.False(result.Success);
        Assert.Contains("既に存在します", result.Message);
    }

    // ===== 補助 =====

    private static string[] Subjects(IReadOnlyList<GitLogRow> rows) =>
        rows.Where(row => row.IsCommit).Select(row => row.Subject!).ToArray();

    private async Task<string?> CurrentUpstreamAsync()
    {
        var result = await _git.RunAsync("rev-parse", "--abbrev-ref", "--symbolic-full-name", "main@{upstream}");
        return result.Success ? result.Output.Trim() : null;
    }

    private async Task<string> CommitAsync(string name, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, name), content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }

    /// <summary>「他の人」の作業コピーを用意する（同じリモートを共有する別クローン）。</summary>
    private async Task<string> CloneForOtherPersonAsync()
    {
        var other = Path.Combine(_base, "other-clone");
        await MustRunInAsync(_base, "clone", _remote, "other-clone");
        await MustRunInAsync(other, "config", "user.name", "Other");
        await MustRunInAsync(other, "config", "user.email", "other@example.invalid");
        return other;
    }

    private static async Task<GitCommandResult> MustRunInAsync(string workingDirectory, params string[] args)
    {
        var result = await RunAsync(workingDirectory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }

    /// <summary>作業ディレクトリを指定して git を直接動かす（ベアリポジトリ・別クローンの用意用）。</summary>
    private static async Task<GitCommandResult> RunAsync(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 日本語のコミット件名を読むので、GitCommandRunner と同じく UTF-8 で受け取る
            // （既定だとコンソールのコードページで復号されて文字化けする）
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }
}
