using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペイン左列の下段（タグ／リモート／サブモジュール）の切替。3つを縦に積むと本命の
/// ブランチ一覧が潰れるので切替式にした、その選択の持ち越しと、消えたタブの扱い。
/// </summary>
public sealed class GitReferenceTabTests
{
    private static GitSessionViewModel CreateVm(LoomoSettings? settings = null, SettingsStore? store = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-git-ref-tab", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var git = new GitService(workspace);
        var query = new GitSessionQuery(git);
        return new GitSessionViewModel(git, new FakeEditorService(), query,
            new GitSessionCommandHandler(git), new GitHistoryViewModel(query),
            new GitRootSwitchViewModel(git, workspace), settings, store);
    }

    [Fact]
    public void 既定はタグ()
    {
        Assert.Equal(GitReferenceTab.Tags, CreateVm().ReferenceTab);
    }

    [Fact]
    public void 保存された種類で初期化される()
    {
        var vm = CreateVm(new LoomoSettings { GitReferenceTab = "Remotes" });

        Assert.Equal(GitReferenceTab.Remotes, vm.ReferenceTab);
        Assert.Equal(GitReferenceTab.Remotes, vm.EffectiveReferenceTab);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Branches")]      // 知らない値（手書き・将来の版）
    // 数値文字列は Enum.TryParse が「成功」させて定義の無い値を返す。落とさないと
    // どのタブにも一致せず、左列の下段が空白のまま操作不能になる。
    [InlineData("7")]
    public void 読めない値は既定へ落とす(string stored)
    {
        // ここで例外にすると、settings.json を1文字間違えただけで起動ごと落ちる。
        Assert.Equal(GitReferenceTab.Tags, CreateVm(new LoomoSettings { GitReferenceTab = stored }).ReferenceTab);
    }

    [Fact]
    public void 切り替えると設定ファイルへ永続化される()
    {
        var settings = new LoomoSettings();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-settings.json");
        var store = new SettingsStore(path);
        try
        {
            var vm = CreateVm(settings, store);

            vm.ReferenceTab = GitReferenceTab.Submodules;

            Assert.Equal("Submodules", settings.GitReferenceTab);
            var reloaded = new LoomoSettings();
            store.Load(reloaded);
            Assert.Equal("Submodules", reloaded.GitReferenceTab);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void サブモジュールが無いときは表示だけタグへ落として選択は覚えておく()
    {
        // 選択まで書き換えると、サブモジュールのあるワークスペースへ戻ったときに元へ戻らない。
        var vm = CreateVm(new LoomoSettings { GitReferenceTab = "Submodules" });

        Assert.Equal(GitReferenceTab.Submodules, vm.ReferenceTab);
        Assert.False(vm.HasSubmodules);
        Assert.Equal(GitReferenceTab.Tags, vm.EffectiveReferenceTab);

        vm.Submodules = new[]
        {
            new GitSubmoduleInfo("libs/a", "abc1234", null, false, false, false),
        };

        Assert.True(vm.HasSubmodules);
        Assert.Equal(GitReferenceTab.Submodules, vm.EffectiveReferenceTab);
    }

    [Theory]
    [InlineData(GitReferenceTab.Tags, false, GitReferenceTab.Tags)]
    [InlineData(GitReferenceTab.Remotes, true, GitReferenceTab.Remotes)]
    [InlineData(GitReferenceTab.Remotes, false, GitReferenceTab.Remotes)]
    [InlineData(GitReferenceTab.Submodules, false, GitReferenceTab.Tags)]
    [InlineData(GitReferenceTab.Submodules, true, GitReferenceTab.Submodules)]
    public void 表示する種類の決め方(GitReferenceTab desired, bool hasSubmodules, GitReferenceTab expected)
    {
        Assert.Equal(expected, GitSessionViewModel.Resolve(desired, hasSubmodules));
    }
}
