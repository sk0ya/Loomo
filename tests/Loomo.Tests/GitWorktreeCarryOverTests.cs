using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ワークツリーへ Git に載らないものを引き継ぐ（§24.48）。フォルダーはジャンクション・ファイルはコピー、
/// 既にあるものは飛ばす、消す前にはリンクだけ外して先の中身を残す。</summary>
public sealed class GitWorktreeCarryOverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-carry-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "main");
    private string Target => Path.Combine(_root, "wt");

    public GitWorktreeCarryOverTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(Source, "node_modules", "pkg", "index.js"), "module.exports = 1;");
        File.WriteAllText(Path.Combine(Source, ".env"), "KEY=1");
        Directory.CreateDirectory(Path.Combine(Source, "src", "App"));
        File.WriteAllText(Path.Combine(Source, "src", "App", "appsettings.Development.json"), "{}");
        Directory.CreateDirectory(Target);
    }

    public void Dispose()
    {
        // ジャンクションの先を消さないよう、リンクを先に外してから消す。
        GitWorktreeCarryOver.Unlink(Target, Path.Combine(_root, "record"));
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 区切りを読み_外へ出るものは捨てる()
        => Assert.Equal(["node_modules", ".env", @"src\App\a.json"],
            GitWorktreeCarryOver.Parse("node_modules; .env\n src/App/a.json ;..\\x; C:\\y; .git\\hooks; NODE_MODULES"));

    [Fact]
    public void フォルダーはリンク_ファイルはコピー_既にあるものと無いものは飛ばす()
    {
        File.WriteAllText(Path.Combine(Target, ".env"), "KEY=already");
        var record = Path.Combine(_root, "record");

        var result = GitWorktreeCarryOver.Apply(Source, Target,
            ["node_modules", ".env", @"src\App\appsettings.Development.json", "missing"], record);

        Assert.Equal(["node_modules"], result.Linked);
        Assert.Equal([@"src\App\appsettings.Development.json"], result.Copied);
        Assert.Equal([".env", "missing"], result.Skipped);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(Path.Combine(Target, "node_modules", "pkg", "index.js")));
        Assert.True(new DirectoryInfo(Path.Combine(Target, "node_modules")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("KEY=already", File.ReadAllText(Path.Combine(Target, ".env")));
        Assert.Equal(["node_modules"], File.ReadAllLines(record));
    }

    [Fact]
    public void 外すとリンクだけ消え_先の中身は残る()
    {
        var record = Path.Combine(_root, "record");
        GitWorktreeCarryOver.Apply(Source, Target, ["node_modules"], record);

        Assert.Equal(1, GitWorktreeCarryOver.Unlink(Target, record));

        Assert.False(Directory.Exists(Path.Combine(Target, "node_modules")));
        Assert.True(File.Exists(Path.Combine(Source, "node_modules", "pkg", "index.js")));
    }

    [Fact]
    public void 控えにあっても本物のフォルダーは消さない()
    {
        var record = Path.Combine(_root, "record");
        Directory.CreateDirectory(Path.Combine(Target, "node_modules"));
        File.WriteAllLines(record, ["node_modules"]);

        Assert.Equal(0, GitWorktreeCarryOver.Unlink(Target, record));
        Assert.True(Directory.Exists(Path.Combine(Target, "node_modules")));
    }
}
