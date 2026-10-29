using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペインの幅（左列・詳細列・グラフ・一覧の列・操作ログの詳細列）。境界のドラッグで決めた幅は
/// 設定へ残って次に開いたときに戻り、ペインが狭まっても脇の列は画面の外へ押し出されず、
/// 列境界のドラッグは境界がマウスから離れない。
/// </summary>
public sealed class GitPaneLayoutTests
{
    [Fact]
    public void 決めた幅は設定へ残り読み直すと戻る()
    {
        var settings = new LoomoSettings();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-settings.json");
        var store = new SettingsStore(path);
        try
        {
            var vm = CreateVm(settings, store);
            vm.Layout.BranchColumnWidth = 260;
            vm.Layout.CommitDetailWidth = 420;
            vm.Layout.ReflogDetailWidth = 380;
            vm.Layout.GraphWidth = 48;
            vm.Layout.LogColumnWidths[GitLogColumnLayout.Author] = 150;
            vm.SaveLayout();

            var reloaded = new LoomoSettings();
            store.Load(reloaded);
            var again = CreateVm(reloaded, null);
            Assert.Equal(260, again.Layout.BranchColumnWidth);
            Assert.Equal(420, again.Layout.CommitDetailWidth);
            Assert.Equal(380, again.Layout.ReflogDetailWidth);
            Assert.Equal(48, again.Layout.GraphWidth);
            Assert.Equal(48, again.History.GraphWidth); // グラフ幅は開いた時点で一覧へ効いている
            Assert.Equal(150, again.Layout.LogColumnWidths[GitLogColumnLayout.Author]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 手書きで壊れた幅は既定へ落とす()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-settings.json");
        try
        {
            File.WriteAllText(path, """
                { "gitLayout": { "branchColumnWidth": -5, "commitDetailWidth": 0, "graphWidth": 30,
                                 "logColumnWidths": { "Date": -1, "Id": 70 } } }
                """);
            var settings = new LoomoSettings();
            new SettingsStore(path).Load(settings);

            Assert.Null(settings.GitLayout.BranchColumnWidth);
            Assert.Null(settings.GitLayout.CommitDetailWidth);
            Assert.Equal(30, settings.GitLayout.GraphWidth);
            Assert.Equal(new Dictionary<string, double> { ["Id"] = 70 }, settings.GitLayout.LogColumnWidths);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(1000, 206, 140, 794)]  // 広い：中央の最小幅を引いた残りまで
    [InlineData(300, 206, 140, 140)]   // 狭すぎる：脇の最小幅は割らない（はみ出すのはこのときだけ）
    public void 脇の列の上限は中央の最小幅を残せる幅(double grid, double others, double min, double expected)
        => Assert.Equal(expected, GitSessionColumnVisibilityPresenter.SideColumnMaxWidth(grid, others, min));

    [Fact]
    public void まだ測られていない間は脇の列に上限を付けない()
        => Assert.Equal(double.PositiveInfinity, GitSessionColumnVisibilityPresenter.SideColumnMaxWidth(0, 206, 140));

    [Theory]
    [InlineData(100, 25, 125)]    // 動かした分だけ（足し込みではなく掴んだ幅から）
    [InlineData(100, -90, 30)]    // 最小幅で止まる
    [InlineData(100, 500, 180)]   // 余りを吸う列が最小幅に達したら止まる（横スクロールを出さない）
    public void 列境界のドラッグ幅は掴んだ幅と移動量で決まり最小最大で止まる(double start, double moved, double expected)
        => Assert.Equal(expected, GitLogColumnLayoutPolicy.DraggedColumnWidth(start, moved, 30, 180));

    [Fact]
    public void 最小で止まった後に戻すとすぐ境界が付いてくる()
    {
        // 最小幅を超えて引き込んだ後、少し戻しただけでは広がらない（境界は掴んだ点からの位置で決まる）。
        Assert.Equal(30, GitLogColumnLayoutPolicy.DraggedColumnWidth(100, -80, 30, 180));
        Assert.Equal(40, GitLogColumnLayoutPolicy.DraggedColumnWidth(100, -60, 30, 180));
    }

    private static GitSessionViewModel CreateVm(LoomoSettings? settings, SettingsStore? store)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-git-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var git = new GitService(workspace);
        var query = new GitSessionQuery(git);
        return new GitSessionViewModel(git, new FakeEditorService(), query, new GitSessionCommandHandler(git),
            new GitHistoryViewModel(query), new GitRootSwitchViewModel(git, workspace), settings, store);
    }
}
