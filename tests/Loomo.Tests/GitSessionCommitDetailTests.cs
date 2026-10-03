using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>Git ペインのコミット詳細の表示ON/OFF（タイトル領域のトグル）と、その設定への永続化。</summary>
public sealed class GitSessionCommitDetailTests
{
    private static GitSessionViewModel CreateVm(LoomoSettings? settings, SettingsStore? store)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-git-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var git = new GitService(workspace);
        var editor = new FakeEditorService();
        var query = new GitSessionQuery(git);
        return new GitSessionViewModel(git, editor, query, new GitSessionCommandHandler(git),
            new GitHistoryViewModel(query), new GitRootSwitchViewModel(git, workspace), settings, store);
    }

    [Fact]
    public void 保存された表示状態で初期化され設定が無ければ表示が既定()
    {
        Assert.True(CreateVm(null, null).CommitDetailVisible);
        Assert.False(CreateVm(new LoomoSettings { GitCommitDetailVisible = false }, null).CommitDetailVisible);
    }

    [Fact]
    public void 切り替えると設定ファイルへ永続化される()
    {
        var settings = new LoomoSettings { GitCommitDetailVisible = true };
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-settings.json");
        var store = new SettingsStore(path);
        try
        {
            var vm = CreateVm(settings, store);

            vm.CommitDetailVisible = false;

            Assert.False(settings.GitCommitDetailVisible);
            // 読み直しても OFF が残る。
            var reloaded = new LoomoSettings();
            store.Load(reloaded);
            Assert.False(reloaded.GitCommitDetailVisible);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 本体の面は履歴が既定で読めない値も履歴に落ちる()
    {
        Assert.Equal(GitSessionMode.History, CreateVm(null, null).Mode);
        Assert.Equal(GitSessionMode.History, CreateVm(new LoomoSettings { GitSessionMode = "7" }, null).Mode);
        Assert.Equal(GitSessionMode.Reflog, CreateVm(new LoomoSettings { GitSessionMode = "Reflog" }, null).Mode);
    }

    [Fact]
    public void 面の切替は押し直しで外れず設定へ持ち越される()
    {
        var settings = new LoomoSettings();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-settings.json");
        var store = new SettingsStore(path);
        try
        {
            var vm = CreateVm(settings, store);

            vm.IsReflogMode = true;
            Assert.True(vm.IsReflogMode);
            Assert.False(vm.IsHistoryModeChecked);
            // ラジオの「外れた」書き込みは無視する（どちらかが必ず選ばれている）。
            vm.IsReflogMode = false;
            Assert.Equal(GitSessionMode.Reflog, vm.Mode);

            var reloaded = new LoomoSettings();
            store.Load(reloaded);
            Assert.Equal("Reflog", reloaded.GitSessionMode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
