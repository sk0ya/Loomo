using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Editor.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Diff の右側と Editor ペインのタブは<b>同じ文書</b>（§24.16）。右はファイルを持たず、Editor のタブの
/// 本文を映して一緒に編集する。実物の git リポジトリと実物のエディタで、どちらで打っても両方に出ること・
/// 保存が Editor のタブの保存になること・Diff を離れても何も聞かず編集がタブに残ることを確かめる。
/// （確認ダイアログが出る実装だと、このテストは MessageBox で止まる。）
/// </summary>
public class DiffSideEditorPresenterTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "loomo-diffdoc-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_repo, "a.txt");

    public DiffSideEditorPresenterTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        File.WriteAllText(FilePath, "one\ntwo\nthree\n");
        Git("add", "a.txt");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        File.WriteAllText(FilePath, "one\nTWO\nthree\n");   // 未ステージの変更
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* 一時フォルダの後始末は best effort */ }
    }

    [Fact]
    public void Editorで開いているファイルは右で打つとタブにも入りタブで打つと右にも入る()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var tab = docs.OpenAsTab(FilePath);
            var (presenter, _) = Show(docs);

            var right = presenter.Right!;
            Assert.Equal(tab.Text, right.Text);

            Type(right, "three", "three!");
            Assert.Equal("one\nTWO\nthree!\n", Norm(tab.Text));
            Assert.True(tab.IsModified);

            Type(tab, "one", "zero");
            Assert.Equal("zero\nTWO\nthree!\n", Norm(right.Text));
            Assert.Equal(0, docs.OpenCount);   // 既に開いているタブを使う
            presenter.Dispose();
        });
    }

    [Fact]
    public void Editorで開いていないファイルは右で打った時点でタブを足し離れても編集はタブに残る()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var (presenter, vm) = Show(docs);
            Assert.Equal(0, docs.OpenCount);   // 見るだけならタブは増やさない

            Type(presenter.Right!, "three", "edited");

            Assert.Equal(1, docs.OpenCount);
            var tab = docs.Find(FilePath)!;
            Assert.Equal("one\nTWO\nedited\n", Norm(tab.Text));
            Assert.True(tab.IsModified);
            Assert.True(presenter.HasUnsavedEdits);

            // Diff を閉じる：何も聞かない（聞くなら MessageBox で止まる）。編集は Editor のタブに残る。
            presenter.Dispose();
            Assert.Equal("one\nTWO\nedited\n", Norm(tab.Text));
            Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(FilePath));
            _ = vm;
        });
    }

    [Fact]
    public void 右での保存はEditorのタブの保存になる()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var (presenter, _) = Show(docs);

            Type(presenter.Right!, "three", "saved");
            Assert.True(presenter.SaveRight());
            PumpUntil(() => docs.SaveCount > 0);

            var tab = docs.Find(FilePath)!;
            Assert.Equal("one\nTWO\nsaved\n", Norm(File.ReadAllText(FilePath)));
            Assert.False(tab.IsModified);
            Assert.False(presenter.HasUnsavedEdits);
            presenter.Dispose();
        });
    }

    [Fact]
    public void 映していたタブが閉じられたら右はディスクの本文に戻る()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var (presenter, vm) = Show(docs);
            Type(presenter.Right!, "three", "discarded");
            Assert.Equal(1, docs.OpenCount);
            docs.Close(FilePath);   // Editor 側で「保存しない」で閉じた（Diff の行は変わらない＝Sync は走らない）

            Assert.Equal("one\nTWO\nthree\n", Norm(presenter.Right!.Text));
            Assert.False(presenter.HasUnsavedEdits);

            // 続けて打っても、破棄した編集は戻ってこない
            Type(presenter.Right!, "one", "uno");
            Assert.Equal("uno\nTWO\nthree\n", Norm(docs.Find(FilePath)!.Text));
            _ = vm;
            presenter.Dispose();
        });
    }

    [Fact]
    public void 映しているタブがディスクから読み直されたら右も追従し古い本文で上書きしない()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var tab = docs.OpenAsTab(FilePath);
            var (presenter, _) = Show(docs);

            File.WriteAllText(FilePath, "one\nTWO\nthree\nfrom-branch\n");   // ブランチ切替でディスクが変わった
            docs.Reload(FilePath);

            Assert.Equal("one\nTWO\nthree\nfrom-branch\n", Norm(presenter.Right!.Text));
            Type(presenter.Right!, "one", "uno");
            Assert.Equal("uno\nTWO\nthree\nfrom-branch\n", Norm(tab.Text));
            presenter.Dispose();
        });
    }

    [Fact]
    public void Diffを開いた後にEditorで開いたタブは右が映し未保存の編集を消さない()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var (presenter, _) = Show(docs);

            var tab = docs.OpenAsTab(FilePath);   // Editor で開いた（読み込みの知らせで右が映す）
            Type(tab, "one", "EDITED");
            Assert.Equal("EDITED\nTWO\nthree\n", Norm(presenter.Right!.Text));
            Assert.True(presenter.HasUnsavedEdits);

            Type(presenter.Right!, "three", "3");
            Assert.Equal("EDITED\nTWO\n3\n", Norm(tab.Text));
            Assert.Equal(0, docs.OpenCount);
            presenter.Dispose();
        });
    }

    [Fact]
    public void 映す前にタブが編集されていたら右で打ってもタブの本文を上書きしない()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var (presenter, _) = Show(docs);

            // 知らせが届く前に Editor 側で編集された状況（Sync も走っていない）
            var tab = docs.OpenAsTab(FilePath, announce: false);
            Type(tab, "one", "EDITED");
            Assert.True(presenter.HasUnsavedEdits);   // まだ映していなくても数える＝範囲の破棄を止める

            Type(presenter.Right!, "three", "3");

            Assert.Equal("EDITED\nTWO\nthree\n", Norm(tab.Text));          // タブの編集は残る
            Assert.Equal("EDITED\nTWO\nthree\n", Norm(presenter.Right!.Text)); // 右はタブの本文を映す
            Assert.Equal(0, docs.OpenCount);
            presenter.Dispose();
        });
    }

    [Fact]
    public void Editorのタブで保存したら右も保存済みになる()
    {
        RunSta(() =>
        {
            var docs = new FakeDocuments();
            var tab = docs.OpenAsTab(FilePath);
            var (presenter, _) = Show(docs);

            Type(presenter.Right!, "three", "3");
            Assert.True(presenter.Right!.IsModified);

            _ = docs.SaveAsync(tab);   // Editor ペインで Ctrl+S

            Assert.False(tab.IsModified);
            Assert.False(presenter.Right!.IsModified);
            presenter.Dispose();
        });
    }

    [Fact]
    public void 切り離し窓の複製は片方の保存でもう片方も保存済みになる()
    {
        RunSta(() =>
        {
            var events = new EditorDocumentEvents();
            var source = new VimEditorControl();
            source.LoadFile(FilePath);
            var mirror = new VimEditorControl();
            mirror.LoadFile(FilePath);
            using var sync = new EditorTextMirror(source, mirror, events);

            Type(source, "one", "uno");
            Assert.True(mirror.IsModified);   // 編集として入る

            source.Save();
            events.RaiseSaved(source);

            Assert.False(mirror.IsModified);
            Assert.Equal(Norm(source.Text), Norm(mirror.Text));
        });
    }

    // ===== 組み立て =====

    private (DiffSideEditorPresenter Presenter, DiffSessionViewModel Vm) Show(FakeDocuments docs)
    {
        var workspace = new FakeWorkspaceService(_repo);
        var git = new GitService(workspace);
        var vm = new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
        var presenter = new DiffSideEditorPresenter(() => vm, new Border(), new Border(), new VimStatusBar());
        presenter.Configure(() => new VimEditorControl(), _ => { }, docs);

        var show = vm.ShowWorkingTreeFileAsync(
            new GitChangeEntry("a.txt", null, ' ', 'M', IsUntracked: false, IsConflicted: false), isStaged: false);
        PumpUntil(() => show.IsCompleted && vm.EditableSidePath is not null && vm.SideRows.Count > 0);
        presenter.Sync();
        return (presenter, vm);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("条件が満たされないまま時間切れ");
            PumpDispatcher();
            Thread.Sleep(10);
        }
    }

    /// <summary>エンジンを通る編集（打鍵と同じく BufferChanged が出る）。<c>SetText</c> は読み込み扱いで出ない。</summary>
    private static void Type(VimEditorControl editor, string from, string to)
        => editor.ExecuteCommand($"%s/{from}/{to}/");

    /// <summary>末尾改行の持ち方（空の最終行）と CRLF の違いを均す。</summary>
    private static string Norm(string text)
    {
        text = text.Replace("\r\n", "\n");
        return text.EndsWith('\n') ? text : text + "\n";
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

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>Editor ペインの代わり：パスごとに実物のエディタを1つ持ち、ShellWindow と同じ所で知らせを出す
    /// （読み込み＝AfterEditorFileLoaded、保存＝EditorService.FileSaved、閉じる＝CloseEditorTab）。</summary>
    private sealed class FakeDocuments : IDiffWorkingDocuments
    {
        private readonly Dictionary<string, VimEditorControl> _tabs = new(StringComparer.OrdinalIgnoreCase);
        public EditorDocumentEvents Events { get; } = new();
        public int OpenCount { get; private set; }
        public int SaveCount { get; private set; }

        /// <param name="announce">false なら読み込みの知らせを出さない（知らせより先に打たれた場合の再現）。</param>
        public VimEditorControl OpenAsTab(string path, bool announce = true)
        {
            var control = new VimEditorControl();
            control.LoadFile(path);
            _tabs[Path.GetFullPath(path)] = control;
            if (announce) Events.RaiseLoaded(control);
            return control;
        }

        /// <summary>ブランチ切替・一括置換と同じ：開いているタブへディスクから読み直す。</summary>
        public void Reload(string path)
        {
            var control = _tabs[Path.GetFullPath(path)];
            control.LoadFile(path);
            control.ExecuteCommand("e!");
            Events.RaiseLoaded(control);
        }

        public void Close(string path)
        {
            var key = Path.GetFullPath(path);
            var control = _tabs[key];
            _tabs.Remove(key);
            Events.RaiseClosed(control);
        }

        public VimEditorControl? Find(string path)
            => _tabs.TryGetValue(Path.GetFullPath(path), out var c) ? c : null;

        public VimEditorControl? Open(string path)
        {
            OpenCount++;
            return OpenAsTab(path);
        }

        public Task SaveAsync(VimEditorControl document)
        {
            document.Save();
            SaveCount++;
            Events.RaiseSaved(document);
            return Task.CompletedTask;
        }

        public bool IsOpen(VimEditorControl document) => _tabs.ContainsValue(document);
    }
}
