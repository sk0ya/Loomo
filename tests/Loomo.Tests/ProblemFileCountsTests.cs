using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>問題の数をファイルツリーとタブに出す（§24.33）。</summary>
public sealed class ProblemFileCountsTests
{
    private static ProblemItemViewModel P(string path, int line, ProblemSeverity severity, string code = "CS0001",
        ProblemSource source = ProblemSource.Lsp)
        => new(path, line, 1, severity, code, "msg", source);

    [Fact]
    public void ファイルごとに数え_フォルダーには配下の合計が付く()
    {
        var counts = ProblemFileCounts.From([
            P(@"C:\work\app\src\a.cs", 1, ProblemSeverity.Error),
            P(@"C:\work\app\src\a.cs", 2, ProblemSeverity.Warning),
            P(@"C:\work\app\src\sub\b.cs", 3, ProblemSeverity.Warning),
            P(@"C:\work\app\src\c.cs", 4, ProblemSeverity.Hint),
        ]);

        Assert.Equal(new ProblemMark(1, 1), counts.For(@"C:\work\app\src\a.cs"));
        Assert.Equal(new ProblemMark(0, 1), counts.For(@"C:\work\app\src\sub\b.cs"));
        Assert.Equal(new ProblemMark(1, 2), counts.For(@"C:\work\app\src\"));
        Assert.Equal(new ProblemMark(1, 2), counts.For(@"C:\work\app"));
        // ヒント・情報は印にしない（全部のファイルに付いてしまう）。
        Assert.Null(counts.For(@"C:\work\app\src\c.cs"));
    }

    [Fact]
    public void IDEとTSIDEの両方が持つ同じ診断は1件に数える()
    {
        var fromDotnet = P(@"C:\w\a.ts", 1, ProblemSeverity.Error, "TS2304");
        var fromTs = P(@"C:\w\a.ts", 1, ProblemSeverity.Error, "TS2304");

        var counts = ProblemFileCounts.From([fromDotnet, fromTs]);

        Assert.Equal(new ProblemMark(1, 0), counts.For(@"C:\w\a.ts"));
    }

    [Fact]
    public void 印の文字はエラーがあればエラーの数_無ければ警告の数()
    {
        Assert.Equal("2", new ProblemMark(2, 5).Text);
        Assert.True(new ProblemMark(2, 5).HasErrors);
        Assert.Equal("5", new ProblemMark(0, 5).Text);
        Assert.Equal("エラー 2 件・警告 5 件", new ProblemMark(2, 5).ToolTip);
    }

    [Fact]
    public void 同じ中身なら同じとみなす()
    {
        var a = ProblemFileCounts.From([P(@"C:\w\a.cs", 1, ProblemSeverity.Error)]);
        var b = ProblemFileCounts.From([P(@"C:\w\a.cs", 1, ProblemSeverity.Error)]);
        var c = ProblemFileCounts.From([P(@"C:\w\a.cs", 1, ProblemSeverity.Warning)]);

        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(c));
        Assert.True(ProblemFileCounts.From([]).SameAs(ProblemFileCounts.Empty));
    }

    [Fact]
    public void タブの一覧は開いたタブにも後から開いたタブにも印を付ける()
    {
        var tabs = new TabsViewModel(new TabIconService(), new LoomoSettings());
        tabs.AddEditorTab(Guid.NewGuid(), @"C:\w\a.cs", isModified: false, isActive: true);

        tabs.ApplyProblemCounts(ProblemFileCounts.From([
            P(@"C:\w\a.cs", 1, ProblemSeverity.Error), P(@"C:\w\b.cs", 1, ProblemSeverity.Warning)]));
        tabs.AddEditorTab(Guid.NewGuid(), @"C:\w\b.cs", isModified: false, isActive: false);

        Assert.Equal(new ProblemMark(1, 0), tabs.EditorTabs[0].ProblemMark);
        Assert.Equal(new ProblemMark(0, 1), tabs.EditorTabs.Single(t => t.FilePath == @"C:\w\b.cs").ProblemMark);
    }

    [Fact]
    public void Problemsは組み直すたびに知らせる()
    {
        var problems = new ProblemsViewModel();
        var raised = 0;
        problems.Changed += () => raised++;

        problems.SetFromBuildOutput(@"C:\w\a.cs(1,1): error CS0001: x");

        Assert.Equal(1, raised);
    }
}
