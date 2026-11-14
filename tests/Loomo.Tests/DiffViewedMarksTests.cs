using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>差分のファイルの「見た」印（§24.107）。印はパッチの中身に付き、中身が変われば外れる。</summary>
public sealed class DiffViewedMarksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loomo-diffviewed-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_dir, "repo");
    private string StoreFile => Path.Combine(_dir, "diff-viewed.json");
    private string FilePath => Path.Combine(Repo, "a.txt");

    private const string Patch =
        "diff --git a/a.txt b/a.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/a.txt\n" +
        "+++ b/a.txt\n" +
        "@@ -1,3 +1,3 @@\n" +
        " 1\n" +
        "-2\n" +
        "+two\n" +
        " 3\n";

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public void 指紋はコミットのハッシュと行番号に左右されず_中身が変われば変わる()
    {
        var rebased = Patch.Replace("index 1111111..2222222", "index 3333333..4444444").Replace("@@ -1,3 +1,3 @@", "@@ -40,3 +40,3 @@");
        var changed = Patch.Replace("+two", "+TWO");

        Assert.Equal(DiffViewedMarks.Fingerprint(Patch), DiffViewedMarks.Fingerprint(rebased));
        Assert.Equal(DiffViewedMarks.Fingerprint(Patch), DiffViewedMarks.Fingerprint(Patch.Replace("\n", "\r\n")));
        Assert.NotEqual(DiffViewedMarks.Fingerprint(Patch), DiffViewedMarks.Fingerprint(changed));
    }

    [Fact]
    public void 状態は覚えた指紋で決まり_コミット範囲は変わったとは言わない()
    {
        Assert.Equal(DiffViewedState.None, DiffViewedMarks.StateOf("wt", null, "a"));
        Assert.Equal(DiffViewedState.Viewed, DiffViewedMarks.StateOf("wt", ["x", "a"], "a"));
        Assert.Equal(DiffViewedState.ChangedSinceViewed, DiffViewedMarks.StateOf("wt", ["x"], "a"));
        Assert.Equal(DiffViewedState.None, DiffViewedMarks.StateOf("range", ["x"], "a"));
    }

    [Fact]
    public void 見方は作業ツリー_比較基準_コミット範囲で分かれ_アドホック比較には付けない()
    {
        var wt = new DiffFileItem
        {
            FullPath = @"C:\r\a.txt", DisplayPath = "a.txt", Badge = "M",
            Entry = new GitChangeEntry("a.txt", null, ' ', 'M', IsUntracked: false, IsConflicted: false),
        };
        var compare = new DiffFileItem
        {
            FullPath = "", DisplayPath = "A ↔ B", Badge = "",
            Comparison = new DiffComparison("A", "x", "B", "y"),
        };

        Assert.Equal("wt", DiffViewedMarks.Scope(wt, null));
        Assert.Null(DiffViewedMarks.Scope(compare, null));
    }

    [Fact]
    public void 次の未読は今の次から回って探し_全部見ていたら無い()
    {
        DiffViewedState[] states = [DiffViewedState.Viewed, DiffViewedState.None, DiffViewedState.Viewed, DiffViewedState.ChangedSinceViewed];

        Assert.Equal(3, DiffViewedMarks.NextUnviewed(states, 1));
        Assert.Equal(1, DiffViewedMarks.NextUnviewed(states, 3));   // 末尾から先頭へ回る
        Assert.Equal(1, DiffViewedMarks.NextUnviewed(states, -1));
        Assert.Equal(-1, DiffViewedMarks.NextUnviewed([DiffViewedState.Viewed, DiffViewedState.Viewed], 0));
    }

    [Fact]
    public void 見出しの数は見たものが無ければ出さない()
    {
        Assert.Equal("", DiffViewedMarks.Summary([DiffViewedState.None, DiffViewedState.None]));
        Assert.Equal("見た 1/3", DiffViewedMarks.Summary([DiffViewedState.Viewed, DiffViewedState.None, DiffViewedState.None]));
        Assert.Equal("見た 1/3・変わった 1",
            DiffViewedMarks.Summary([DiffViewedState.Viewed, DiffViewedState.ChangedSinceViewed, DiffViewedState.None]));
    }

    [Fact]
    public void 置き場は開き直しても残り_外せば消え_指紋は上限まで()
    {
        Directory.CreateDirectory(_dir);
        using (var store = new DiffViewedStore(StoreFile, synchronous: true))
        {
            for (var i = 0; i < DiffViewedMarks.MaxFingerprintsPerFile + 2; i++)
                store.MarkViewed("wt", @"C:\r\a.txt", "p" + i);
            store.MarkViewed("wt", @"C:\r\b.txt", "q");
            store.Forget("wt", @"C:\r\b.txt");
        }

        using var reopened = new DiffViewedStore(StoreFile, synchronous: true);
        var prints = reopened.Get("wt", @"c:\R\A.TXT");   // 大文字小文字は区別しない
        Assert.NotNull(prints);
        Assert.Equal(DiffViewedMarks.MaxFingerprintsPerFile, prints!.Count);
        Assert.Equal("p" + (DiffViewedMarks.MaxFingerprintsPerFile + 1), prints[^1]);
        Assert.Null(reopened.Get("wt", @"C:\r\b.txt"));
        Assert.Null(reopened.Get("base:MergeBase:main", @"C:\r\a.txt"));   // 見方が違えば別
    }

    [Fact]
    public void 実物のリポジトリで_見たあとに書き換えると変わったになり_付け直すと見たに戻り_開き直しても残る() => RunSta(() =>
    {
        InitRepo();
        using var store = new DiffViewedStore(StoreFile, synchronous: true);
        var vm = Show(store);
        var node = Assert.Single(DiffFileTreeNode.Leaves(vm.FileTree));
        Assert.Equal(DiffViewedState.None, node.ViewedState);
        Assert.True(node.CanMarkViewed);

        var toggle = vm.ToggleViewedCommand.ExecuteAsync(vm.SelectedFile);
        PumpUntil(() => toggle.IsCompleted);
        Assert.Equal(DiffViewedState.Viewed, node.ViewedState);
        Assert.Contains("見た 1/1", vm.FileListHeader);

        // 見たあとに後ろで書き換えられた（行数は同じ＝一覧の見た目は変わらない）
        File.WriteAllText(FilePath, "1\nTWO\n3\n");
        var again = vm.ShowWorkingTreeFileAsync(Entry());
        PumpUntil(() => again.IsCompleted
            && DiffFileTreeNode.Leaves(vm.FileTree).Single().ViewedState == DiffViewedState.ChangedSinceViewed);
        Assert.Contains("変わった 1", vm.FileListHeader);

        var mark = vm.MarkViewedAndNextCommand.ExecuteAsync(null);
        PumpUntil(() => mark.IsCompleted);
        Assert.Equal(DiffViewedState.Viewed, DiffFileTreeNode.Leaves(vm.FileTree).Single().ViewedState);

        // 別の VM（部屋を開き直した・切り離し窓）でも同じ置き場なら見たまま
        var reopened = Show(store);
        PumpUntil(() => DiffFileTreeNode.Leaves(reopened.FileTree).Single().ViewedState == DiffViewedState.Viewed);
    });

    private void InitRepo()
    {
        Directory.CreateDirectory(Repo);
        Git("init", "-q");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(FilePath, "1\n2\n3\n");
        Git("add", "a.txt");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        File.WriteAllText(FilePath, "1\ntwo\n3\n");
    }

    private static GitChangeEntry Entry() => new("a.txt", null, ' ', 'M', IsUntracked: false, IsConflicted: false);

    private DiffSessionViewModel Show(DiffViewedStore store)
    {
        var workspace = new FakeWorkspaceService(Repo);
        var git = new GitService(workspace);
        var vm = new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git), store);
        var show = vm.ShowWorkingTreeFileAsync(Entry());
        PumpUntil(() => show.IsCompleted && vm.Files.Count == 1);
        return vm;
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, UseShellExecute = false };
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
