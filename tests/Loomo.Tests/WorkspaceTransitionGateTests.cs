using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ワークスペース切替の途中に割り込んだ「ファイルを開く」が、前のワークスペースのタブ集合へ入らないこと。
/// 実機で起きていたのは、切替でツリーを入れ替えた拍子に選択プレビューが走り、切替の途中（タブ集合が
/// まだ前のワークスペース）で PDF を開いてしまう——前のワークスペースへタブが紛れ込み、アクティブタブまで
/// 書き換わるので、戻ると EditorSupport が別のワークスペースの PDF を出し続けた。
/// </summary>
public class WorkspaceTransitionGateTests
{
    [Fact]
    public void 切替の外では待たない()
    {
        var gate = new WorkspaceTransitionGate();

        Assert.False(gate.IsSwitching);
        Assert.True(gate.WhenSettledAsync().IsCompleted);
    }

    [Fact]
    public async Task 切替の途中に届いた要求は切替が終わってから進む()
    {
        var gate = new WorkspaceTransitionGate();
        var scope = gate.Begin();

        var waiting = gate.WhenSettledAsync();
        Assert.True(gate.IsSwitching);
        Assert.False(waiting.IsCompleted);

        scope.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsSwitching);
    }

    [Fact]
    public async Task 重なった切替は全部抜けるまで途中のまま()
    {
        // 起動時の復元は直列化の外から入るので、重なっても数えられること。
        var gate = new WorkspaceTransitionGate();
        var outer = gate.Begin();
        var inner = gate.Begin();
        var waiting = gate.WhenSettledAsync();

        inner.Dispose();
        await Task.Delay(50);
        Assert.True(gate.IsSwitching);
        Assert.False(waiting.IsCompleted);

        outer.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsSwitching);
    }

    [Fact]
    public void 待っている間に切替が挟まらなければ続きを進めてよい()
    {
        var gate = new WorkspaceTransitionGate();

        var epoch = gate.Epoch;

        Assert.False(gate.HasSwitchedSince(epoch));
    }

    [Fact]
    public void 切替より前に始まった処理も途中で切替が挟まれば続きを捨てる()
    {
        // 入口で IsSwitching を見るだけでは、ファイル読み込みを待っている間に始まった切替を見逃す。
        var gate = new WorkspaceTransitionGate();
        var epoch = gate.Epoch;

        var scope = gate.Begin();
        Assert.True(gate.HasSwitchedSince(epoch));   // 切替の途中

        scope.Dispose();
        Assert.True(gate.HasSwitchedSince(epoch));   // 切替が終わった後でも、別のワークスペースになっている
    }

    [Fact]
    public void 同じ切替を二度抜けても数を狂わせない()
    {
        var gate = new WorkspaceTransitionGate();
        var outer = gate.Begin();
        var inner = gate.Begin();

        inner.Dispose();
        inner.Dispose();

        Assert.True(gate.IsSwitching);
        outer.Dispose();
        Assert.False(gate.IsSwitching);
    }
}

/// <summary>
/// ツリーの選択プレビューは「据え置きを仕掛けた行が、明けたときもまだ選ばれている」ときだけ開く。
/// 以前は明けた時点で選ばれている行を開いていたので、ツリーの入れ替えで来る「選択が外れた」通知が
/// 据え置きを仕掛け、復元で選ばれた行（＝開かない決まりの行）を切替の途中に開いていた。
/// </summary>
public sealed class FolderTreeSelectionPreviewPolicyTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"loomo-tree-preview-{Guid.NewGuid():N}")).FullName;
    private readonly FakeWorkspaceService _workspace = new();
    private readonly FolderTreeViewModel _tree;

    public FolderTreeSelectionPreviewPolicyTests()
    {
        _workspace.OpenFolder(_root);
        _tree = new FolderTreeViewModel(_workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(_root, "workflows")),
            new FolderTreeCommandHandler(_workspace, new FileOperationHistory()),
            new FolderTreeQuery());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 選択が外れた通知では何も予約しない()
        => Assert.Null(FolderTreeSelectionPreviewPolicy.CandidateFor(null));

    [Fact]
    public void フォルダーの行は開く候補にしない()
        => Assert.Null(FolderTreeSelectionPreviewPolicy.CandidateFor(Node("src", isDirectory: true)));

    [Fact]
    public void ファイルの行は候補になり_選ばれたままなら開く()
    {
        var file = Node("a.pdf");

        var candidate = FolderTreeSelectionPreviewPolicy.CandidateFor(file);

        Assert.Same(file, candidate);
        Assert.True(FolderTreeSelectionPreviewPolicy.ShouldPreview(file, candidate));
    }

    [Fact]
    public void 明けたときに別の行が選ばれていたら開かない()
    {
        // ツリーの入れ替えで前の行が候補のまま残り、明けたときには復元した行が選ばれている。
        var before = Node("before.pdf");
        var restored = Node("restored.md");

        var candidate = FolderTreeSelectionPreviewPolicy.CandidateFor(before);

        Assert.False(FolderTreeSelectionPreviewPolicy.ShouldPreview(restored, candidate));
    }

    [Fact]
    public void 候補が無ければ選ばれている行があっても開かない()
    {
        // 「選択が外れた」通知で仕掛けた据え置きが、明けたときに選ばれている復元行を開いていたのがこれ。
        var restored = Node("restored.pdf");

        var candidate = FolderTreeSelectionPreviewPolicy.CandidateFor(null);

        Assert.False(FolderTreeSelectionPreviewPolicy.ShouldPreview(restored, candidate));
    }

    private FileNodeViewModel Node(string name, bool isDirectory = false)
        => new(Path.Combine(_root, name), isDirectory, _tree, _root);
}
