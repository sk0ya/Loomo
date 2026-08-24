using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>編集可能なアドレス欄はファイル一覧ペインの道具である（サイドバーのツリーではない）。
///
/// <para>ツリーに置かれていたときは、打ち込んだパスが「ワークスペースを切り替える」経路へ流れ、
/// 配下のフォルダーを打っただけでペインもタブもレイアウトも入れ替わっていた。住所は
/// 「いま見ている場所」であって、部屋の作り直しではない——ここではその区別を見る。</para></summary>
public sealed class FilesAddressBarTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _child;
    private readonly string _sibling;
    private readonly string _outside;
    private readonly FakeWorkspaceService _workspace = new();

    public FilesAddressBarTests()
    {
        _base = Path.Combine(Path.GetTempPath(), $"loomo-files-address-{Guid.NewGuid():N}");
        _root = Path.Combine(_base, "ws");
        _child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        _sibling = Directory.CreateDirectory(Path.Combine(_root, "sibling")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(_root, "note.txt"), "x");
        _workspace.OpenFolder(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private FilesColumnViewModel CreateColumn()
    {
        var tree = new FolderTreeViewModel(_workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-files-address-wf-{Guid.NewGuid():N}")),
            new FolderTreeCommandHandler(_workspace, new FileOperationHistory()), new FolderTreeQuery());
        var column = new FilesColumnViewModel(
            _workspace,
            FolderTreeCommandHandler.Unconfined(_workspace, new FileOperationHistory()),
            tree, new FakeFilePlacesProvider());
        column.Restore(snapshot: null, fallbackFolder: _root);
        return column;
    }

    [Fact]
    public void Ctrl_Lで開くと現在地が初期値になりEscで畳む()
    {
        var column = CreateColumn();

        column.BeginAddressEdit();
        Assert.True(column.IsAddressEditing);
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(column.AddressText));

        column.CancelAddressEdit();
        Assert.False(column.IsAddressEditing);
        Assert.Empty(column.AddressSuggestions);
    }

    [Fact]
    public void 相対パスは現在地を基準に解決する()
    {
        var column = CreateColumn();
        column.BeginAddressEdit();

        Assert.True(column.NavigateAddress("child"));

        Assert.Equal(_child, column.CurrentFolder);
        // 移動できたら入力欄は畳んでパンくずへ戻す。
        Assert.False(column.IsAddressEditing);
    }

    [Fact]
    public void ワークスペースの外も同じように歩ける()
    {
        var column = CreateColumn();

        Assert.True(column.NavigateAddress(_outside));

        // ファイル一覧はもともとワークスペース外も歩ける。ここでワークスペースを
        // 切り替えたり作ったりしない（部屋は動かさない）。
        Assert.Equal(_outside, column.CurrentFolder);
        Assert.Equal(new[] { Path.GetFullPath(_root) }, _workspace.Folders.Select(Path.GetFullPath));
    }

    [Fact]
    public void ファイルを指したら親を開いてそのファイルを選ぶ()
    {
        var column = CreateColumn();
        var file = Path.Combine(_root, "note.txt");

        Assert.True(column.NavigateAddress(file));

        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(column.CurrentFolder));
        Assert.Equal(file, column.PendingSelection);
    }

    [Fact]
    public void 存在しないパスは理由を出して移動しない()
    {
        var column = CreateColumn();
        var before = column.CurrentFolder;
        column.BeginAddressEdit();

        Assert.False(column.NavigateAddress("does-not-exist"));

        Assert.True(column.HasAddressError);
        Assert.Contains("存在しません", column.AddressError);
        Assert.Equal(before, column.CurrentFolder);
        // 直せるよう入力欄は開いたままにする。
        Assert.True(column.IsAddressEditing);
    }

    [Fact]
    public void 解釈できない入力も同じ扱いで畳まない()
    {
        var column = CreateColumn();
        column.BeginAddressEdit();

        Assert.False(column.NavigateAddress("   "));

        Assert.True(column.HasAddressError);
        Assert.True(column.IsAddressEditing);
    }

    [Fact]
    public void 候補は区切り文字まで打つと直下のフォルダーを出す()
    {
        var column = CreateColumn();
        column.BeginAddressEdit();

        column.AddressText = _root + Path.DirectorySeparatorChar;

        Assert.Contains(_child, column.AddressSuggestions);
        Assert.Contains(_sibling, column.AddressSuggestions);
    }

    [Fact]
    public void 移動したパスは入力履歴に残る()
    {
        var column = CreateColumn();
        column.NavigateAddress(_child);

        column.BeginAddressEdit();
        column.AddressText = "";

        Assert.Contains(_child, column.AddressSuggestions);
    }

    [Fact]
    public void UNCパスは共有へ触らずそのまま正規化する()
    {
        const string unc = @"\\server\share\folder";

        Assert.True(FolderTreeAddressHistory.TryNormalizePath(unc, null, out var fullPath));
        Assert.Equal(unc, fullPath);
    }

    [Fact]
    public void アドレス欄はファイル一覧にありサイドバーには無い()
    {
        var files = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");
        Assert.Contains("x:Name=\"AddressBox\"", files);
        Assert.Contains("PreviewKeyDown=\"OnAddressKeyDown\"", files);
        Assert.Contains("x:Name=\"AddressSuggestionPopup\"", files);
        // パンくずと入力欄は同じ一行の別の顔（行を2つに増やさない）。
        Assert.Contains("x:Name=\"AddressArea\"", files);
        Assert.Contains("MouseLeftButtonDown=\"OnBreadcrumbBlankClick\"", files);

        var tree = Read("src", "Loomo.App", "Views", "FolderTreeView.xaml");
        Assert.DoesNotContain("AddressComboBox", tree);
        Assert.DoesNotContain("x:Name=\"AddressBar\"", tree);
        // 戻る／進むはアドレス欄の履歴専用だったので、ツリーからは消えている。
        Assert.DoesNotContain("GoBackCommand", tree);
        Assert.DoesNotContain("GoForwardCommand", tree);
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
}
