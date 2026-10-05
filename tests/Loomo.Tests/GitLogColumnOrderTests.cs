using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>Git コミット一覧の列の並び・表示／非表示（見出しの右クリックとドラッグ）と、その設定への永続化。</summary>
public sealed class GitLogColumnOrderTests
{
    private const string C = GitLogColumnLayout.Commit, D = GitLogColumnLayout.Date,
        A = GitLogColumnLayout.Author, I = GitLogColumnLayout.Id;

    [Fact]
    public void 保存値の未知の列と重複は捨て足りない列は既定の位置へ足す()
    {
        Assert.Equal([C, D, A, I], GitLogColumnLayout.NormalizeOrder(null));
        Assert.Equal([I, C, D, A], GitLogColumnLayout.NormalizeOrder([I, "Bogus", C, I, D, A]));
        // 作成者が無い（後から増えた列）→ 既定で直前の「日時」の後ろへ
        Assert.Equal([I, D, A, C], GitLogColumnLayout.NormalizeOrder([I, D, C]));
    }

    [Fact]
    public void コミット列は隠せない()
    {
        Assert.Equal([D], GitLogColumnLayout.NormalizeHidden([C, D, D, "Bogus"]));
    }

    [Fact]
    public void 見えている列の並べ替えは隠した列の位置を動かさない()
    {
        // 作成者を隠したまま、ID を先頭へドラッグした
        var merged = GitLogColumnLayout.MergeVisibleOrder([C, D, A, I], [A], [I, C, D]);
        Assert.Equal([I, C, A, D], merged);
        Assert.Equal([I, C, D], GitLogColumnLayout.Visible(merged, [A]));
    }

    [Fact]
    public void 左右への移動は隠した列を飛び越えて隣の見えている列と入れ替える()
    {
        Assert.Equal([C, I, A, D], GitLogColumnLayout.MoveVisible([C, D, A, I], [A], D, +1));
        // 端ではそのまま
        Assert.Equal([C, D, A, I], GitLogColumnLayout.MoveVisible([C, D, A, I], [], C, -1));
    }

    [Fact]
    public void 列の変更は設定ファイルへ永続化され読み直すと同じ並びで始まる()
    {
        var settings = new LoomoSettings();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-settings.json");
        var store = new SettingsStore(path);
        try
        {
            var vm = CreateVm(settings, store);
            var raised = 0;
            vm.LogColumnsChanged += (_, _) => raised++;

            vm.SetLogColumns([I, C, D, A], [A]);
            vm.SetLogColumns([I, C, D, A], [A]); // 同じ値では通知しない

            Assert.Equal(1, raised);
            var reloaded = new LoomoSettings();
            store.Load(reloaded);
            var again = CreateVm(reloaded, null);
            Assert.Equal([I, C, D, A], again.LogColumnOrder);
            Assert.Equal([A], again.LogHiddenColumns);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
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
