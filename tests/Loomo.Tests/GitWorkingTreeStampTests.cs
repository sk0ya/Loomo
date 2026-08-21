using System;
using System.IO;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="GitRepositoryMonitor"/> のポーリング署名に混ぜる作業ツリーの印。
/// <c>git status --porcelain=v2</c> の行は HEAD と index の object id しか持たないので、
/// すでに変更として出ているファイルの中身がもう一度変わっても出力は変わらない。
/// その分をサイズと更新時刻で見分けられることを確かめる（Diff ペインの差分が古いまま固まる不具合）。
/// </summary>
public class GitWorkingTreeStampTests
{
    private static string ModifiedLine(string path)
        => $"1 .M N... 100644 100644 100644 1111111 2222222 {path}\n";

    private static string UntrackedLine(string path) => $"? {path}\n";

    [Fact]
    public void 同じステータスでも中身が変われば印が変わる()
    {
        var root = Directory.CreateTempSubdirectory("loomo-stamp-test").FullName;
        try
        {
            var file = Path.Combine(root, "a.txt");
            File.WriteAllText(file, "before");
            var status = ModifiedLine("a.txt");

            var first = GitRepositoryMonitor.BuildWorkingTreeStamp(root, status);

            // ステータス出力は1バイトも変わらない状況で、作業ツリーの中身だけが変わる
            File.WriteAllText(file, "before and after");
            var second = GitRepositoryMonitor.BuildWorkingTreeStamp(root, status);

            Assert.NotEqual(first, second);
            // 触らなければ印は安定する（無変化で通知を撒かない）
            Assert.Equal(second, GitRepositoryMonitor.BuildWorkingTreeStamp(root, status));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 変更が無ければ印は空で読めないパスは無変化として扱う()
    {
        var root = Directory.CreateTempSubdirectory("loomo-stamp-test").FullName;
        try
        {
            Assert.Equal("", GitRepositoryMonitor.BuildWorkingTreeStamp(root, "# branch.head main\n"));

            // 消えたファイル・引用符付きの読めないパスでも例外を投げず、同じ入力なら同じ印を返す
            var status = UntrackedLine("missing.txt") + ModifiedLine("\"quoted\\303\\251.txt\"");
            var stamp = GitRepositoryMonitor.BuildWorkingTreeStamp(root, status);
            Assert.Equal(stamp, GitRepositoryMonitor.BuildWorkingTreeStamp(root, status));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 同じファイルがステージと作業ツリーの両方に出ても印は1つ()
    {
        var root = Directory.CreateTempSubdirectory("loomo-stamp-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "x");
            // MM＝ステージ済みの変更と、その上の作業ツリーの変更（両方の一覧に載る）
            var stamp = GitRepositoryMonitor.BuildWorkingTreeStamp(
                root, "1 MM N... 100644 100644 100644 1111111 2222222 a.txt\n");

            Assert.Equal(1, stamp.Split("a.txt", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
