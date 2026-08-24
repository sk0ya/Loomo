using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class GitTreeStateTests
{
    [Fact]
    public void 状態をファイルと親フォルダーへ集約する()
    {
        var root = NewDirectory();
        try
        {
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "loomo@example.test");
            RunGit(root, "config", "user.name", "Loomo Tests");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "tracked.cs"), "original");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.txt\n");
            RunGit(root, "add", ".");
            RunGit(root, "commit", "-m", "initial");

            File.WriteAllText(Path.Combine(root, "src", "tracked.cs"), "modified");
            File.WriteAllText(Path.Combine(root, "staged.cs"), "staged");
            RunGit(root, "add", "staged.cs");
            File.WriteAllText(Path.Combine(root, "untracked.txt"), "new");
            File.WriteAllText(Path.Combine(root, "ignored.txt"), "ignored");

            var state = GitTreeState.Load(root, CancellationToken.None);
            state.GetIgnoredPaths(new[]
            {
                Path.Combine(root, "ignored.txt"),
                Path.Combine(root, "untracked.txt"),
            });

            Assert.Equal(GitChangeKind.Modified,
                state.GetStatus(Path.Combine(root, "src", "tracked.cs"), false));
            Assert.Equal(GitChangeKind.Staged,
                state.GetStatus(Path.Combine(root, "staged.cs"), false));
            Assert.Equal(GitChangeKind.Untracked,
                state.GetStatus(Path.Combine(root, "untracked.txt"), false));
            Assert.Equal(GitChangeKind.Ignored,
                state.GetStatus(Path.Combine(root, "ignored.txt"), false));
            Assert.Equal(GitChangeKind.DirectoryChanged,
                state.GetStatus(Path.Combine(root, "src"), true));
            Assert.Equal(GitChangeKind.None,
                state.GetStatus(Path.Combine(root, "clean.txt"), false));

            // index と作業ツリーの両方に差分がある MM は「ステージ済み」だけに
            // 潰さず、まだ作業ツリー変更が残る Modified を優先する。
            File.AppendAllText(Path.Combine(root, "staged.cs"), "worktree change");
            var mixedState = GitTreeState.Load(root, CancellationToken.None);
            Assert.Equal(GitChangeKind.Modified,
                mixedState.GetStatus(Path.Combine(root, "staged.cs"), false));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void リポジトリ外はクリーンとして扱う()
    {
        var root = NewDirectory();
        try
        {
            var state = GitTreeState.Load(root, CancellationToken.None);
            Assert.False(state.IsGitRepository);
            Assert.Equal(GitChangeKind.None, state.GetStatus(root, true));
            Assert.Equal(GitChangeKind.None,
                state.GetStatus(Path.Combine(root, "file.txt"), false));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void 複数リポジトリの状態を混ぜない()
    {
        var first = NewDirectory();
        var second = NewDirectory();
        try
        {
            InitializeRepository(first, "first.txt");
            InitializeRepository(second, "second.txt");
            File.WriteAllText(Path.Combine(first, "first.txt"), "changed in first");
            File.WriteAllText(Path.Combine(second, "second.txt"), "changed in second");

            var firstState = GitTreeState.Load(first, CancellationToken.None);
            var secondState = GitTreeState.Load(second, CancellationToken.None);
            Assert.Equal(GitChangeKind.Modified,
                firstState.GetStatus(Path.Combine(first, "first.txt"), false));
            Assert.Equal(GitChangeKind.None,
                firstState.GetStatus(Path.Combine(second, "second.txt"), false));
            Assert.Equal(GitChangeKind.Modified,
                secondState.GetStatus(Path.Combine(second, "second.txt"), false));
        }
        finally
        {
            DeleteDirectory(first);
            DeleteDirectory(second);
        }
    }

    [Fact]
    public void 競合状態はステージや削除より優先して競合になる()
    {
        var root = NewDirectory();
        try
        {
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "loomo@example.test");
            RunGit(root, "config", "user.name", "Loomo Tests");
            var path = Path.Combine(root, "conflict.txt");
            File.WriteAllText(path, "base\n");
            RunGit(root, "add", "conflict.txt");
            RunGit(root, "commit", "-m", "initial");

            RunGit(root, "checkout", "-b", "feature");
            File.WriteAllText(path, "feature\n");
            RunGit(root, "commit", "-am", "feature");
            RunGit(root, "checkout", "-");
            File.WriteAllText(path, "main\n");
            RunGit(root, "commit", "-am", "main");
            Assert.NotEqual(0, RunGitAllowFailure(root, "merge", "feature"));

            var state = GitTreeState.Load(root, CancellationToken.None);
            Assert.Equal(GitChangeKind.Conflicted, state.GetStatus(path, false));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void キャンセルされた読込はプロセスを継続しない()
    {
        var root = NewDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => GitTreeState.Load(root, cts.Token));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(GitChangeKind.Modified, "M", "変更")]
    [InlineData(GitChangeKind.Untracked, "U", "未追跡")]
    [InlineData(GitChangeKind.Conflicted, "C", "競合")]
    [InlineData(GitChangeKind.Staged, "S", "ステージ済み")]
    [InlineData(GitChangeKind.Ignored, "I", "無視対象")]
    public void 一覧行のバッジ表示が状態と一致する(GitChangeKind kind, string badge, string tooltip)
    {
        var entry = new FileEntryViewModel("C:\\work\\file.txt", false, 1, DateTime.UtcNow)
        {
            GitStatus = kind,
        };

        Assert.Equal(badge, entry.GitStatusBadge);
        Assert.Equal(tooltip, entry.GitStatusTooltip);
    }

    private static string NewDirectory()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "loomo-git-tree-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void InitializeRepository(string directory, string fileName)
    {
        RunGit(directory, "init");
        RunGit(directory, "config", "user.email", "loomo@example.test");
        RunGit(directory, "config", "user.name", "Loomo Tests");
        File.WriteAllText(Path.Combine(directory, fileName), "original");
        RunGit(directory, "add", fileName);
        RunGit(directory, "commit", "-m", "initial");
    }

    private static void RunGit(string directory, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start());
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }

    private static int RunGitAllowFailure(string directory, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start());
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>一覧を組み立てる経路（FilesColumnViewModel.LoadEntries）が使う
    /// <c>GitStatusForPath</c> は、読み込み済みのキャッシュだけを見ること。
    ///
    /// <para>ここで <c>GetIgnoredPaths</c>（＝<c>git check-ignore</c> の同期起動）を呼ぶと、
    /// フォルダーを開くたびに<b>項目数ぶんの git プロセス</b>を UI スレッドで順番に立てることに
    /// なる。500 個のファイルがあるフォルダーでは 500 回で、画面が描かれる前に固まる。
    /// 無視状態は GitStatusesForPaths がバックグラウンドで一括照会して後から反映する。</para></summary>
    [Fact]
    public void 一覧の1件ずつの状態取得はcheck_ignoreを起動しない()
    {
        var source = ReadSource("src", "Loomo.App", "ViewModels", "FolderTreeViewModel.cs");
        var start = source.IndexOf("internal GitChangeKind GitStatusForPath(", StringComparison.Ordinal);
        Assert.True(start >= 0, "GitStatusForPath が見つからない");
        var batch = source.IndexOf("GitStatusesForPaths(", start, StringComparison.Ordinal);
        Assert.True(batch > start, "GitStatusesForPaths が見つからない");
        var body = source[start..batch];

        Assert.DoesNotContain("GetIgnoredPaths", body);
        // 一括照会のほうは今までどおり起動してよい（バックグラウンド専用）。
        Assert.Contains("GetIgnoredPaths", source[batch..]);
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(SourceFile())!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sk0ya.Loomo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(parts).ToArray()));
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
