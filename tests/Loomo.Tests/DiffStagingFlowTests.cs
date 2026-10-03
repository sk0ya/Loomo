using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Diff ペインでのステージを、実物の git リポジトリと実物の VM で通しで確かめる：
/// ステージしても差分から行が消えず（HEAD↔作業ツリーの1枚のまま）、その行に印が付き、
/// 一覧は1ファイル1項目のまま進み具合だけが変わる。アンステージで元に戻る。
/// </summary>
public class DiffStagingFlowTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "loomo-diffstage-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_repo, "a.txt");

    public DiffStagingFlowTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(FilePath, "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n");
        Git("add", "a.txt");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        // 2か所の変更：2→two（前）と 11→eleven（後ろ）
        File.WriteAllText(FilePath, "1\ntwo\n3\n4\n5\n6\n7\n8\n9\n10\neleven\n12\n");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public void ステージしても行は消えず印が付き_アンステージで戻る() => RunSta(() =>
    {
        var vm = Show();
        Assert.Equal(DiffStageState.None, Assert.Single(vm.Files).Stage);
        var changedRows = vm.SideRows.Count(IsChange);
        Assert.Equal(2, changedRows);

        // 前の変更（旧2行目→新2行目）だけステージ
        var stage = vm.StageLinesAsync(new HashSet<int> { 2 }, new HashSet<int> { 2 }, stage: true);
        PumpUntil(() => stage.IsCompleted && vm.SideRows.Any(r => r.RightStaged));

        Assert.Equal(changedRows, vm.SideRows.Count(IsChange));   // 差分から行が消えない
        var front = vm.SideRows.Single(r => r.RightText == "two");
        var back = vm.SideRows.Single(r => r.RightText == "eleven");
        Assert.True(front.LeftStaged && front.RightStaged);
        Assert.False(back.LeftStaged || back.RightStaged);
        var item = Assert.Single(vm.Files);                      // 一覧は1項目のまま
        Assert.Equal(DiffStageState.Partial, item.Stage);
        Assert.Equal("1\ntwo\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n", Git("show", ":a.txt").Replace("\r\n", "\n"));

        // 同じ行をアンステージ
        var unstage = vm.StageLinesAsync(new HashSet<int> { 2 }, new HashSet<int> { 2 }, stage: false);
        PumpUntil(() => unstage.IsCompleted && !vm.SideRows.Any(r => r.LeftStaged || r.RightStaged)
                        && vm.Files.Single().Stage == DiffStageState.None);
        Assert.Equal(changedRows, vm.SideRows.Count(IsChange));
    });

    [Fact]
    public void 統合表示の行にもステージ済みの印が付く() => RunSta(() =>
    {
        Git("add", "a.txt");
        var vm = Show();
        vm.IsSideBySide = false;
        PumpUntil(() => vm.DiffRows.Any(r => r.Kind is "Added" or "Removed"));
        PumpUntil(() => vm.DiffRows.Where(r => r.Kind is "Added" or "Removed").All(r => r.Staged));

        Assert.Equal(DiffStageState.All, Assert.Single(vm.Files).Stage);
        Assert.Equal(4, vm.DiffRows.Count(r => r.Kind is "Added" or "Removed"));
    });

    private static bool IsChange(DiffSideRowVm row) => row.LeftKind == "Removed" || row.RightKind == "Added";

    private DiffSessionViewModel Show()
    {
        var workspace = new FakeWorkspaceService(_repo);
        var git = new GitService(workspace);
        var vm = new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
        var show = vm.ShowWorkingTreeFileAsync(
            new GitChangeEntry("a.txt", null, ' ', 'M', IsUntracked: false, IsConflicted: false));
        PumpUntil(() => show.IsCompleted && vm.SideRows.Count > 0);
        return vm;
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("条件が満たされないまま時間切れ");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
            throw exception;
    }
}
