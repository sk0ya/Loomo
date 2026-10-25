using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>差分を「この語に触った変更」だけに絞る（§24.85）。</summary>
public sealed class DiffChangeFilterTests
{
    private const string Patch =
        "diff --git a/Logger.cs b/Logger.cs\n" +
        "--- a/Logger.cs\n" +
        "+++ b/Logger.cs\n" +
        "@@ -1,3 +1,3 @@\n" +
        " class Logger\n" +
        "-    void Old() {}\n" +
        "+    void Write() {}\n";

    [Fact]
    public void 追加と削除の行だけを見る()
    {
        Assert.True(DiffChangeFilter.PatchMatches(Patch, "write"));     // 大文字小文字は区別しない
        Assert.True(DiffChangeFilter.PatchMatches(Patch, "Old"));
        Assert.False(DiffChangeFilter.PatchMatches(Patch, "class"));    // 変えていない行にあるだけ
        Assert.False(DiffChangeFilter.PatchMatches(Patch, "b/Logger")); // ファイルの見出しは数えない
        Assert.True(DiffChangeFilter.PatchMatches(Patch, ""));
    }

    [Fact]
    public void 素材2つの比較も変わった行で見る()
    {
        Assert.True(DiffChangeFilter.TextsMatch("a\nkeep\n", "b\nkeep\n", "a"));
        Assert.False(DiffChangeFilter.TextsMatch("a\nkeep\n", "b\nkeep\n", "keep"));
    }

    [Fact]
    public async Task 語を入れると一覧が絞られ_見出しに元の数が出て_消すと戻る()
    {
        var workspace = new FakeWorkspaceService();
        var git = new GitService(workspace);
        var sut = new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
        sut.ShowComparison(new DiffComparison("A", "x\nLogger.Write();\n", "B", "x\nLogger.Flush();\n"));
        sut.ShowComparison(new DiffComparison("C", "y\n", "D", "z\n"));
        Assert.Equal(2, sut.Files.Count);

        sut.ChangeFilter = "logger";
        await WaitUntilAsync(() => sut.Files.Count == 1);

        Assert.Equal("A ↔ B", Assert.Single(sut.Files).DisplayPath);
        Assert.Contains("「logger」を含む 1/2", sut.FileListHeader);

        sut.ClearChangeFilterCommand.Execute(null);
        await WaitUntilAsync(() => sut.Files.Count == 2);
        Assert.DoesNotContain("を含む", sut.FileListHeader);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(30);
        Assert.True(condition());
    }
}
