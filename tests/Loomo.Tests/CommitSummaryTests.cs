using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Git;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミット詳細の解析（<c>git show --numstat --format=CommitSummary.Format</c>）と、
/// それをフォルダ構造へ組み直す木。Git ペインでは変更ファイル一覧をグラフの右へ縦に置くので、
/// パスが省略されないこと・階層が正しく立つことがそのまま表示の正しさになる。
/// </summary>
public sealed class CommitSummaryTests
{
    private const char US = '\u001f';
    private const char Tab = '\t';

    /// <summary>git の出力を組み立てる（%h %an %ad %cn %cd %s %B のあとに numstat が続く）。</summary>
    private static string Output(string committer = "ある人", string committerDate = "2026-08-25 10:00") =>
        string.Join(US,
            "0c92f1e", "ある人", "2026-08-25 10:00", committer, committerDate,
            "サイトアイコンが出ないまま戻らなくなる3件を直す",
            "サイトアイコンが出ないまま戻らなくなる3件を直す\n\n本文の2行目。\n")
        + "\n\n"
        + string.Join('\n', new[]
        {
            $"12{Tab}3{Tab}src/Loomo.App/Views/GitSessionView.xaml",
            $"4{Tab}0{Tab}src/Loomo.App/ViewModels/GitHistoryViewModel.cs",
            $"-{Tab}-{Tab}assets/icon.png",
            $"1{Tab}1{Tab}src/Loomo.Core/Git/{{CommitStatLinks.cs => CommitSummary.cs}}",
        }) + "\n";

    [Fact]
    public void 見出しはコメントが先で素性は1行にまとめる()
    {
        var header = CommitSummary.Parse(Output()).Header;

        Assert.Equal(
            "サイトアイコンが出ないまま戻らなくなる3件を直す\n\n本文の2行目。\n\n0c92f1e  ある人  2026-08-25 10:00",
            header);
        // ファイル一覧は見出しに混ざらない（一覧はツリーで出す）。
        Assert.DoesNotContain("GitSessionView.xaml", header);
    }

    [Fact]
    public void コミッターが作者と違うときだけ行を足す()
    {
        var header = CommitSummary.Parse(Output(committer: "別の人")).Header;

        Assert.EndsWith("\nコミット: 別の人  2026-08-25 10:00", header);
    }

    [Fact]
    public void 書式を通していない出力はそのまま見出しにする()
    {
        // git がエラーメッセージを返したときなど（区切り文字が無い）。
        Assert.Equal("fatal: bad object deadbeef",
            CommitSummary.Parse("\nfatal: bad object deadbeef\n").Header);
    }

    [Fact]
    public void 増減とバイナリとリネームを読み分ける()
    {
        var files = CommitSummary.Parse(Output()).Files;

        Assert.Equal(4, files.Count);
        Assert.Equal(12, files[0].Added);
        Assert.Equal(3, files[0].Deleted);
        Assert.Equal("+12 -3", files[0].ChurnLabel);

        var binary = files.Single(f => f.Path == "assets/icon.png");
        Assert.True(binary.IsBinary);
        Assert.Equal("Bin", binary.ChurnLabel);

        // リネームは「変更後」を開く（表示は git の綴りのまま残す）。
        var renamed = files.Single(f => f.IsRenamed);
        Assert.Equal("src/Loomo.Core/Git/CommitSummary.cs", renamed.Path);
        Assert.Equal("src/Loomo.Core/Git/{CommitStatLinks.cs => CommitSummary.cs}", renamed.DisplayPath);
    }

    [Fact]
    public void ファイル一覧をフォルダ階層へ組み直す()
    {
        var roots = CommitFileNode.Build(CommitSummary.Parse(Output()).Files);

        Assert.Equal(new[] { "assets", "src" }, roots.Select(n => n.Name).ToArray());

        var src = roots.Single(n => n.Name == "src");
        Assert.Equal(3, src.LeafCount);
        // フォルダ1つだけの連なりはまとめる（src/Loomo.Core/Git）。
        Assert.Equal(new[] { "Loomo.App", "Loomo.Core/Git" }, src.Children.Select(n => n.Name).ToArray());

        var git = src.Children.Single(n => n.Name == "Loomo.Core/Git");
        var leaf = Assert.Single(git.Children);
        Assert.False(leaf.IsDirectory);
        Assert.Equal("CommitSummary.cs", leaf.Name);
        Assert.Equal("src/Loomo.Core/Git/CommitSummary.cs", leaf.NavigatePath);
    }

    [Fact]
    public void 空の詳細では見出しも一覧も空になる()
    {
        var summary = CommitSummary.Parse("");
        Assert.Equal("", summary.Header);
        Assert.Empty(summary.Files);
        Assert.Empty(CommitFileNode.Build(summary.Files));
    }
}
