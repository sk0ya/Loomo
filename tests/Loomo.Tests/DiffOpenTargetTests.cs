using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 差分の表示要求（<see cref="DiffOpenTarget"/>）——「何を見せるか」だけを持ち、出し先（Diff ペイン／
/// ペインが隠れているときの別ウィンドウ）は持たない、という約束の確認。窓のタブ名とアイコンの元に
/// なるので、パスの無い要求でも見出しが壊れないことまで見る。
/// </summary>
public class DiffOpenTargetTests
{
    /// <summary>比較モードは git に触らないので、実体のまま組み立ててよい（DiffComparisonTests と同じ）。</summary>
    private static DiffSessionViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var git = new GitService(workspace);
        return new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
    }

    [Fact]
    public void タブ名は短く組む()
    {
        // タブは何枚も並ぶので、飾りの語は落としハッシュは7桁に詰める。
        Assert.Equal("a.cs@abc1234",
            new DiffOpenTarget.CommitFile("abc1234def567", "コミット abc1234", @"C:\work\a.cs", 0).WindowTitle);
        Assert.Equal("a.cs",        // 作業ツリーは部屋の既定の文脈なので名乗らない
            new DiffOpenTarget.WorkingTreeFile(Entry("src/a.cs"), IsStaged: false).WindowTitle);
        Assert.Equal("@abc1234",
            new DiffOpenTarget.CommitRange(null, "abc1234def567", "コミット abc1234").WindowTitle);
        Assert.Equal("@abc1234→def5678",
            new DiffOpenTarget.CommitRange("abc1234def", "def5678abc", "abc1234 → def5678").WindowTitle);
        Assert.Equal("A ↔ クリップボード", new DiffOpenTarget.Comparison(Compare()).WindowTitle);
    }

    [Fact]
    public void 長い見出しは詰める()
    {
        // 説明的な比較の名前（「a.cs（保存済み）」等）がそのまま並ぶとタブが読めなくなる。
        var target = new DiffOpenTarget.Comparison(new DiffComparison(
            "とても長い左側の見出しです長い長い", "l", "とても長い右側の見出しです長い長い", "r"));

        Assert.Equal("とても長い左側の見出し… ↔ とても長い右側の見出し…", target.WindowTitle);

        var commit = new DiffOpenTarget.CommitFile(
            "abc1234def", "コミット abc1234", "とても長い名前のファイルですね_これは長い.cs", 0);
        Assert.Equal(28, commit.WindowTitle.Length);
        Assert.EndsWith("…", commit.WindowTitle);
    }

    [Fact]
    public void パスが無い要求でも見出しはコミットを名乗る()
    {
        // 「 — 」だけが残った見出しや、空のタブ名を作らない。
        var target = new DiffOpenTarget.CommitFile("abc1234def", "コミット abc1234", null, 0);
        Assert.Equal("@abc1234", target.WindowTitle);
        Assert.Equal("", target.IconPath);
    }

    [Fact]
    public void タブ名は窓の中で見ているファイルへ追従する()
    {
        // 「次の差分」で隣のファイルへ移ったとき、どのコミットの中に居るかは残す。
        var target = new DiffOpenTarget.CommitFile("abc1234def", "コミット abc1234", @"C:\work\a.cs", 0);

        Assert.Equal("b.cs@abc1234", target.TitleFor(@"C:\work\sub\b.cs"));
    }

    [Fact]
    public void アイコンの元は出どころのファイル()
    {
        Assert.Equal("src/a.cs", new DiffOpenTarget.WorkingTreeFile(Entry("src/a.cs"), false).IconPath);
        Assert.Equal(@"C:\work\a.cs", new DiffOpenTarget.Comparison(Compare(@"C:\work\a.cs")).IconPath);
    }

    [Fact]
    public async Task 比較の要求はShowAsyncでそのまま比較として開く()
    {
        // ペインの VM でも切り離しウィンドウの VM でも、同じ要求が同じように開くことが要点。
        var sut = CreateSut();

        await sut.ShowAsync(new DiffOpenTarget.Comparison(Compare()));

        Assert.True(sut.IsCompareMode);
        Assert.Equal("A ↔ クリップボード", sut.SelectedFile?.DisplayPath);
    }

    private static GitChangeEntry Entry(string path)
        => new(path, null, ' ', 'M', IsUntracked: false, IsConflicted: false);

    private static DiffComparison Compare(string filePath = "")
        => new("A", "A の元", "クリップボード", "A の案", filePath);
}
