using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>所有者が違うために git が拒否したリポジトリ（§24.91）。</summary>
public sealed class GitSafeDirectoryTests
{
    // Git for Windows 2.4x の実際の文面（setup.c の die_upon_dubious_ownership）。
    private const string WindowsMessage =
        "fatal: detected dubious ownership in repository at 'D:/shared/repo'\n" +
        "'D:/shared/repo' is owned by:\n" +
        "\tBUILTIN/Administrators (S-1-5-32-544)\n" +
        "but the current user is:\n" +
        "\tPC/koya (S-1-5-21-1-2-3-1001)\n" +
        "To add an exception for this directory, call:\n" +
        "\n" +
        "\tgit config --global --add safe.directory D:/shared/repo\n";

    [Fact]
    public void 拒否の知らせからgitが勧める表記のパスを読む()
        => Assert.Equal("D:/shared/repo", GitSafeDirectory.ParseDubiousOwnership(WindowsMessage));

    [Fact]
    public void 勧める行が無ければrepository_atのパスを使う()
        => Assert.Equal("D:/shared/repo",
            GitSafeDirectory.ParseDubiousOwnership("fatal: detected dubious ownership in repository at 'D:/shared/repo'\n"));

    [Fact]
    public void 引用符で囲まれた勧めの行は引用符を外す()
        => Assert.Equal("D:/my repo",
            GitSafeDirectory.ParseDubiousOwnership(
                "fatal: detected dubious ownership in repository at 'D:/my repo'\n" +
                "\tgit config --global --add safe.directory 'D:/my repo'\n"));

    [Fact]
    public void ほかの失敗は拒否とみなさない()
    {
        Assert.Null(GitSafeDirectory.ParseDubiousOwnership("fatal: not a git repository (or any of the parent directories): .git"));
        Assert.Null(GitSafeDirectory.ParseDubiousOwnership(""));
        Assert.Null(GitSafeDirectory.ParseDubiousOwnership(null));
    }

    [Fact]
    public void 信頼はユーザー全体の設定へsafe_directoryを足す()
        => Assert.Equal(new[] { "config", "--global", "--add", "safe.directory", "D:/shared/repo" },
            GitSafeDirectory.TrustArguments("D:/shared/repo"));

    [Fact]
    public void Gitペインの文面は拒否なら信頼を_そうでなければ初期化を勧める()
    {
        Assert.Contains("信頼できます", GitSessionViewModel.NotRepositoryText("D:/shared/repo"));
        Assert.Contains("D:/shared/repo", GitSessionViewModel.NotRepositoryText("D:/shared/repo"));
        Assert.Contains("初期化できます", GitSessionViewModel.NotRepositoryText(null));
    }
}
