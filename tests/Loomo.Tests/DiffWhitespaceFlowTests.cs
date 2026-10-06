using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 「空白無視」を、実物の git リポジトリと実物の VM で通しで確かめる：字下げを付け直しただけの行が差分から消え、
/// 本当の変更だけが残る。左には旧側の本当の字下げが出る（-w の文脈行は新側の綴りしか持たないため、
/// 左右並びは手元で比べ直している）。行番号は実ファイルのままなので、残った変更だけをステージできる。
/// </summary>
public class DiffWhitespaceFlowTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "loomo-diffws-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_repo, "a.cs");

    private const string Original = "class A\n{\nvoid F()\n{\nG();\n}\nint x = 1;\n}\n";
    // 中身の字下げを付け直し（4行）、本当の変更は x = 1 → x = 2 の1か所だけ。
    private const string Edited = "class A\n{\n    void F()\n    {\n        G();\n    }\n    int x = 2;\n}\n";

    public DiffWhitespaceFlowTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(FilePath, Original);
        Git("add", "a.cs");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        File.WriteAllText(FilePath, Edited);
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
    public void 左右並びで空白無視にすると本当の変更だけが残り_左は旧側の綴りのまま() => RunSta(() =>
    {
        var vm = Show();
        Assert.Equal(5, vm.SideRows.Count(r => r.RightKind == "Added"));

        vm.IgnoreWhitespace = true;
        PumpUntil(() => vm.SideRows.Count(r => r.RightKind == "Added") == 1);

        var change = vm.SideRows.Single(r => r.RightKind == "Added");
        Assert.Equal(("int x = 1;", "    int x = 2;"), (change.LeftText, change.RightText));
        Assert.Equal(("7", "7"), (change.LeftLine, change.RightLine));   // 行番号は実ファイルの行
        // 字下げだけ変わった行は文脈行になり、左右それぞれの綴りを持つ。
        var reindented = vm.SideRows.Single(r => r.LeftText == "G();");
        Assert.Equal(("Context", "        G();"), (reindented.LeftKind, reindented.RightText));

        vm.IgnoreWhitespace = false;
        PumpUntil(() => vm.SideRows.Count(r => r.RightKind == "Added") == 5);
    });

    [Fact]
    public void 統合表示で空白無視にすると本当の変更だけが残る() => RunSta(() =>
    {
        var vm = Show();
        vm.IsSideBySide = false;
        PumpUntil(() => vm.DiffRows.Count(r => r.Kind == "Added") == 5);

        vm.IgnoreWhitespace = true;
        PumpUntil(() => vm.DiffRows.Count(r => r.Kind == "Added") == 1);
        Assert.Equal("+    int x = 2;", vm.DiffRows.Single(r => r.Kind == "Added").Text);
        Assert.Equal("-int x = 1;", vm.DiffRows.Single(r => r.Kind == "Removed").Text);
    });

    [Fact]
    public void 空白無視のままで残った変更だけをステージできる() => RunSta(() =>
    {
        var vm = Show();
        vm.IgnoreWhitespace = true;
        PumpUntil(() => vm.SideRows.Count(r => r.RightKind == "Added") == 1);

        var stage = vm.StageLinesAsync(new HashSet<int> { 7 }, new HashSet<int> { 7 }, stage: true);
        PumpUntil(() => stage.IsCompleted && vm.SideRows.Any(r => r.RightStaged));

        // インデックスには x = 2 の1行だけが入り、字下げの付け直しは入らない。
        Assert.Equal("class A\n{\nvoid F()\n{\nG();\n}\n    int x = 2;\n}\n", Git("show", ":a.cs").Replace("\r\n", "\n"));
    });

    [Fact]
    public void 統合表示で空白無視のときもステージの印が行に揃う() => RunSta(() =>
    {
        // -w のパッチの行と印付けに使うパッチの行がずれると、別の行に印が付く。
        Git("add", "a.cs");
        var vm = Show();
        vm.IsSideBySide = false;
        vm.IgnoreWhitespace = true;
        PumpUntil(() => vm.DiffRows.Count(r => r.Kind is "Added" or "Removed") == 2
                        && vm.DiffRows.Where(r => r.Kind is "Added" or "Removed").All(r => r.Staged));
        Assert.DoesNotContain(vm.DiffRows, r => r.Kind == "Context" && r.Staged);
    });

    [Fact]
    public async Task git_の差分取得は空白無視を渡せる()
    {
        var workspace = new FakeWorkspaceService(_repo);
        var git = new GitService(workspace);
        var entry = new GitChangeEntry("a.cs", null, ' ', 'M', IsUntracked: false, IsConflicted: false);

        var plain = await git.GetHeadDiffTextAsync(entry, 3);
        var ignored = await git.GetHeadDiffTextAsync(entry, 3, ignoreWhitespace: true);

        Assert.Contains("+        G();", plain);
        Assert.DoesNotContain("G();", ignored.Split('\n').Where(l => l.StartsWith('+') || l.StartsWith('-')));
        Assert.Contains("+    int x = 2;", ignored);
    }

    private DiffSessionViewModel Show()
    {
        var workspace = new FakeWorkspaceService(_repo);
        var git = new GitService(workspace);
        var vm = new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
        var show = vm.ShowWorkingTreeFileAsync(
            new GitChangeEntry("a.cs", null, ' ', 'M', IsUntracked: false, IsConflicted: false));
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
