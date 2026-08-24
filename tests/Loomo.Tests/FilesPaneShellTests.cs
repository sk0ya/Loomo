using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>ロードマップの「Windowsシェル連携」はファイル一覧（エクスプローラー相当）の話だが、
/// 実装はサイドバーのツリーにだけ入っていた。ファイル一覧からも同じ操作へ届くことを見る。
/// 実体はツリーと同じ DI インスタンスであること（クイックアクセスの状態が2つに割れないこと）も含む。</summary>
public sealed class FilesPaneShellTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly FakeWorkspaceService _workspace = new();
    private readonly FakeQuickAccess _quickAccess = new() { Available = true };
    private readonly RecordingShellOperations _shell = new();
    // 履歴は DI で 1 個（シングルトン）。ツリーとファイル一覧はこれを共有する。
    private readonly FileOperationHistory _history = new();
    private readonly FolderTreeViewModel _tree;

    public FilesPaneShellTests()
    {
        _base = Path.Combine(Path.GetTempPath(), $"loomo-files-shell-{Guid.NewGuid():N}");
        _root = Path.Combine(_base, "ws");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        _workspace.OpenFolder(_root);

        _tree = new FolderTreeViewModel(_workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-files-shell-wf-{Guid.NewGuid():N}")),
            new FolderTreeCommandHandler(_workspace, _history), new FolderTreeQuery(),
            shellOperations: _shell, quickAccess: _quickAccess);
        _tree.LoadRoot(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private FilesColumnViewModel CreateColumn()
    {
        var column = new FilesColumnViewModel(
            _workspace,
            FolderTreeCommandHandler.Unconfined(_workspace, _history),
            _tree, new FakeFilePlacesProvider(), _tree);
        column.Restore(snapshot: null, fallbackFolder: _root);
        return column;
    }

    // 一覧の行は自前で組む。実際の一覧は Git 状態の非同期取得が UI スレッドへ戻る前提で
    // 書かれており（WPF では常にそうなる）、SynchronizationContext の無いテストでは
    // その戻りが別スレッドで走って行の並びが不安定になる。ここで見たいのは
    // 「選択をどこへ渡すか」なので、行の生成そのものには依存しない。
    private FileEntryViewModel Entry(string name, bool isDirectory = false)
        => new(Path.Combine(_root, name), isDirectory, 0, DateTime.Now);

    [Fact]
    public void シェル操作の実体はツリーと同じインスタンスを使う()
    {
        var column = CreateColumn();

        // 別インスタンスを new すると、クイックアクセスの状態がツリーと一覧で割れる。
        Assert.Same(_tree.ShellOperations, column.ShellOperations);
        Assert.Same(_tree.QuickAccess, column.QuickAccess);
        Assert.Same(_tree.FileProperties, column.FileProperties);
    }

    [Fact]
    public void アプリで開く_共有_送るを選択パスへ渡す()
    {
        var column = CreateColumn();
        var paths = new[] { Path.Combine(_root, "a.txt"), Path.Combine(_root, "b.txt") };

        foreach (var action in new[] { ShellFileAction.OpenWith, ShellFileAction.Share, ShellFileAction.SendTo })
            column.ShellOperations.Execute(action, paths);

        Assert.Equal(
            new[] { ShellFileAction.OpenWith, ShellFileAction.Share, ShellFileAction.SendTo },
            _shell.Calls.Select(call => call.Action));
        Assert.All(_shell.Calls, call => Assert.Equal(paths, call.Paths));
    }

    [Fact]
    public async Task 選択項目をZIPにまとめて履歴から取り消せる()
    {
        var column = CreateColumn();
        var entries = new[] { Entry("a.txt"), Entry("b.txt") };

        var archive = await column.CompressEntriesAsync(entries);

        Assert.True(File.Exists(archive));
        Assert.Equal(_root, Path.GetDirectoryName(archive));

        // 履歴はツリーと共有しているので、ツリー側の Undo でも生成物が消える。
        _tree.UndoFileOperation();
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public void クイックアクセスへのピン留めはフォルダーだけを対象にする()
    {
        var column = CreateColumn();
        var folder = Entry("docs", isDirectory: true);
        var file = Entry("a.txt");

        Assert.False(column.CanPinToQuickAccess(new[] { file }));
        Assert.True(column.CanPinToQuickAccess(new[] { folder }));

        var pinned = column.PinToQuickAccess(new[] { folder, file });
        Assert.Equal(1, pinned.SucceededCount);
        Assert.False(pinned.HasFailures);
        Assert.Equal(new[] { folder.FullPath }, _quickAccess.Pinned);

        // 留めたあとは「留める」が消えて「解除」が出る。
        Assert.False(column.CanPinToQuickAccess(new[] { folder }));
        Assert.True(column.CanUnpinFromQuickAccess(new[] { folder }));

        var unpinned = column.UnpinFromQuickAccess(new[] { folder });
        Assert.Equal(1, unpinned.SucceededCount);
        Assert.Empty(_quickAccess.Pinned);
    }

    [Fact]
    public void 右クリックとキー操作の入口がファイル一覧側にもある()
    {
        var xaml = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");
        foreach (var header in new[] { "アプリで開く…", "共有", "送る", "ZIPに圧縮", "プロパティ",
                                       "クイックアクセスにピン留め", "クイックアクセスから解除" })
            Assert.Contains($"Header=\"{header}\"", xaml);

        Assert.Contains("PreviewTextInput=\"OnListPreviewTextInput\"", xaml);

        var code = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml.cs");
        // Alt+Enter＝プロパティ、j/k＝上下移動、文字入力＝type-ahead 選択。
        Assert.Contains("ShowProperties()", code);
        Assert.Contains("case Key.J:", code);
        Assert.Contains("case Key.K:", code);
        Assert.Contains("FolderTreeKeyboardNavigation.FindTypeAheadMatch", code);
    }

    private static string Read(params string[] parts)
    {
        var root = RepoRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        var root = directory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sk0ya.Loomo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    private sealed record ShellCall(ShellFileAction Action, IReadOnlyList<string> Paths);

    private sealed class RecordingShellOperations : IShellFileOperations
    {
        public List<ShellCall> Calls { get; } = new();

        public ShellFileOperationResult Execute(
            ShellFileAction action, IEnumerable<string> paths, CancellationToken cancellationToken = default)
        {
            var list = paths.ToList();
            Calls.Add(new ShellCall(action, list));
            return new(action, list, Array.Empty<string>(), false, null);
        }
    }

    private sealed class FakeQuickAccess : IQuickAccessService
    {
        public bool Available { get; set; }
        public List<string> Pinned { get; } = new();
        public bool IsAvailable => Available;
        public bool IsPinned(string path) => Pinned.Contains(path, StringComparer.OrdinalIgnoreCase);
        public bool CanPin(string path) => Available && Directory.Exists(path) && !IsPinned(path);

        public QuickAccessOperationResult Pin(string path)
        {
            if (!CanPin(path))
                return new(QuickAccessOperationStatus.AlreadyInRequestedState);
            Pinned.Add(path);
            return new(QuickAccessOperationStatus.Succeeded);
        }

        public QuickAccessOperationResult Unpin(string path)
        {
            var index = Pinned.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return new(QuickAccessOperationStatus.AlreadyInRequestedState);
            Pinned.RemoveAt(index);
            return new(QuickAccessOperationStatus.Succeeded);
        }

        public QuickAccessBatchResult PinMany(IEnumerable<string> paths) => Apply(paths, Pin);

        public QuickAccessBatchResult UnpinMany(IEnumerable<string> paths) => Apply(paths, Unpin);

        public void Invalidate() { }

        private static QuickAccessBatchResult Apply(
            IEnumerable<string> paths, Func<string, QuickAccessOperationResult> action)
        {
            var succeeded = 0;
            var failed = new List<string>();
            foreach (var path in paths)
            {
                if (action(path).Succeeded) succeeded++;
                else failed.Add(path);
            }
            return new(succeeded, failed, failed.Count == 0 ? null : "fake failure");
        }
    }
}
