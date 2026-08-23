using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// フォルダーツリーの右クリックメニューから呼ばれる ViewModel 側の入口
/// （「複製」＝<see cref="FolderTreeViewModel.DuplicateEntry"/>、
/// 「パスをコピー」＞「相対パス」＝<see cref="FolderTreeViewModel.RelativePathFor"/>、
/// 「配下をすべて折りたたむ」＝<see cref="FolderTreeViewModel.CollapseSubtree"/>）の検証。
/// メニューの並び・出し分けそのものは View 側なので、ここでは対象の決まり方だけを見る。
/// </summary>
public sealed class FolderTreeContextMenuTests : IDisposable
{
    private readonly string _primary;
    private readonly string _secondary;

    public FolderTreeContextMenuTests()
    {
        _primary = Path.Combine(Path.GetTempPath(), $"loomo-ctxmenu-{Guid.NewGuid():N}");
        _secondary = Path.Combine(Path.GetTempPath(), $"loomo-ctxmenu-{Guid.NewGuid():N}-b");
        Directory.CreateDirectory(_primary);
        Directory.CreateDirectory(_secondary);
    }

    public void Dispose()
    {
        try { Directory.Delete(_primary, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        try { Directory.Delete(_secondary, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private static (FolderTreeViewModel Sut, IWorkspaceService Workspace) CreateSut()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        var sut = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
        return (sut, workspace);
    }

    [Fact]
    public async Task 複製は同じフォルダーにコピー名を付けて作る()
    {
        File.WriteAllText(Path.Combine(_primary, "note.md"), "hello");

        var (sut, _) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();

        var created = sut.DuplicateEntry(sut.Nodes.Single(n => n.Name == "note.md"));

        Assert.Equal(Path.Combine(_primary, "note - コピー.md"), created);
        Assert.Equal("hello", File.ReadAllText(created!));
    }

    /// <summary>右クリック「元に戻す」の入口。逆操作そのものは FileOperationHistoryTests で見るので、
    /// ここは「ツリーが作り直され、開いているタブが追従できるよう EntryRenamed が飛ぶ」ところだけ。</summary>
    [Fact]
    public async Task 名前の変更を元に戻すとタブ追従の通知が飛ぶ()
    {
        File.WriteAllText(Path.Combine(_primary, "note.md"), "hello");

        var (sut, _) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        sut.RenameEntry(sut.Nodes.Single(n => n.Name == "note.md"), "renamed.md");

        EntryRenamedEventArgs? renamed = null;
        sut.EntryRenamed += (_, e) => renamed = e;
        var result = sut.UndoFileOperation();

        Assert.Equal(Path.Combine(_primary, "renamed.md"), renamed?.OldPath);
        Assert.Equal(Path.Combine(_primary, "note.md"), renamed?.NewPath);
        Assert.Equal(Path.Combine(_primary, "note.md"), result.RevealPath);
        Assert.True(File.Exists(Path.Combine(_primary, "note.md")));
    }

    /// <summary>見出しノード（ワークスペースフォルダー自身）は複製しない——複製先がその親＝
    /// ワークスペース外になるため。View 側は CanDuplicate で項目を隠すが、入口でも拒む。</summary>
    [Fact]
    public async Task 複製はワークスペースフォルダー自身を対象にしない()
    {
        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        var header = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_secondary));

        Assert.True(header.IsWorkspaceFolderRoot);
        Assert.False(header.CanDuplicate);
        Assert.Null(sut.DuplicateEntry(header));
        Assert.False(Directory.Exists(_secondary + " - コピー"));
    }

    [Fact]
    public async Task 相対パスは所属するワークスペースフォルダーが基準になる()
    {
        Directory.CreateDirectory(Path.Combine(_secondary, "src"));
        File.WriteAllText(Path.Combine(_secondary, "src", "app.ts"), "");

        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        var header = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_secondary));
        var src = header.Children.Single(c => c.Name == "src");
        src.IsExpanded = true;   // 遅延読込を起こす
        var file = src.Children.Single(c => c.Name == "app.ts");

        // プライマリ基準なら "..\...\src\app.ts" のような使えないパスになる（マルチルートの定番の間違い）。
        Assert.Equal(Path.Combine("src", "app.ts"), sut.RelativePathFor(file));
    }

    [Fact]
    public async Task 配下をすべて折りたたむはそのフォルダー自身は開いたままにする()
    {
        Directory.CreateDirectory(Path.Combine(_primary, "a", "b"));

        var (sut, _) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();

        var a = sut.Nodes.Single(n => n.Name == "a");
        sut.ExpandSubtree(a);
        var b = a.Children.Single(c => c.Name == "b");
        Assert.True(a.IsExpanded);
        Assert.True(b.IsExpanded);

        sut.CollapseSubtree(a);

        Assert.True(a.IsExpanded);    // 直下の子は見えたまま
        Assert.False(b.IsExpanded);
    }

    // ===== 区切り線の出し分け（FolderTreeView.NormalizeSeparators） =====
    // このメニューは項目の大半が条件付き表示なので、XAML に静的に置いた区切り線をそのまま出すと
    // 「先頭・末尾に線」「線が2本続く」といった見え方になる。

    [Fact]
    public void 区切り線は前後に可視項目があるときだけ残る()
    {
        RunSta(() =>
        {
            var menu = BuildMenu("sep", "hidden", "sep", "visible", "sep", "visible", "sep", "hidden", "sep");

            FolderTreeView.NormalizeSeparators(menu);

            // 残るのは可視項目に挟まれた3本目だけ。先頭・空グループの手前・末尾は消える。
            Assert.Equal(new[] { false, false, true, false, false }, Separators(menu));
        });
    }

    [Fact]
    public void グループが丸ごと隠れたら区切り線も消える()
    {
        RunSta(() =>
        {
            var menu = BuildMenu("visible", "sep", "hidden", "hidden", "sep", "visible");

            FolderTreeView.NormalizeSeparators(menu);

            // 間のグループが空なので線は1本だけ（2本続けて出さない）。
            Assert.Equal(new[] { false, true }, Separators(menu));
        });
    }

    /// <summary>"sep"＝区切り線、"visible"/"hidden"＝その表示状態の項目。</summary>
    private static ItemsControl BuildMenu(params string[] spec)
    {
        var menu = new ContextMenu();
        foreach (var item in spec)
            menu.Items.Add(item == "sep"
                ? new Separator()
                : new MenuItem
                {
                    Header = item,
                    Visibility = item == "visible" ? Visibility.Visible : Visibility.Collapsed,
                });
        return menu;
    }

    private static bool[] Separators(ItemsControl menu)
        => menu.Items.OfType<Separator>().Select(s => s.Visibility == Visibility.Visible).ToArray();

    private static void RunSta(Action body)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { ex = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }
}
