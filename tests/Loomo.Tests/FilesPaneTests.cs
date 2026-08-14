using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ファイル一覧（エクスプローラ）ペインの検証。ツリーが不得手な仕事——並べ替え・絞り込み・
/// 1フォルダーの平らな一覧——が正しく出ること、ワークスペースの外も同じように扱えること、
/// ピン留めがツリーと同じ一覧になること、復元（§24.4）でカラム構成と現在地が戻ることを見る。
/// 表示の見た目は対象外。
/// </summary>
public sealed class FilesPaneTests : IDisposable
{
    // ワークスペースは _base の中に置く（「上へ」でワークスペースの外へ出る先を、
    // %TEMP% 直下のような賑やかな場所にしないため）。
    private readonly string _base;
    private readonly string _root;
    private readonly string _sub;
    private readonly string _outside;
    private readonly FakeWorkspaceService _workspace = new();
    private readonly FolderTreeViewModel _tree;

    public FilesPaneTests()
    {
        _base = Path.Combine(Path.GetTempPath(), $"loomo-files-{Guid.NewGuid():N}");
        _root = Path.Combine(_base, "ws");
        _sub = Path.Combine(_root, "src");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_sub);
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(_outside);
        WriteFile(Path.Combine(_root, "file2.txt"), "22", new DateTime(2026, 1, 2));
        WriteFile(Path.Combine(_root, "file10.txt"), "1", new DateTime(2026, 3, 4));
        WriteFile(Path.Combine(_root, "app.cs"), new string('x', 4096), new DateTime(2025, 12, 1));
        WriteFile(Path.Combine(_sub, "inner.cs"), "", new DateTime(2026, 2, 1));
        WriteFile(Path.Combine(_outside, "外部.txt"), "external", new DateTime(2026, 5, 5));
        _workspace.OpenFolder(_root);

        // ピン留めの持ち主はツリー。ペインは IFolderPinStore 越しに同じものを見る（§26.10）。
        _tree = new FolderTreeViewModel(_workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(_workspace), new FolderTreeQuery());
        _tree.LoadRoot(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private static void WriteFile(string path, string content, DateTime modified)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, modified);
    }

    private FilesColumnViewModel CreateColumn()
    {
        var column = new FilesColumnViewModel(
            _workspace, FolderTreeCommandHandler.Unconfined(_workspace), _tree, new FakeFilePlacesProvider());
        column.Restore(snapshot: null, fallbackFolder: _root);
        return column;
    }

    private FilesPaneViewModel CreatePane()
    {
        var pane = new FilesPaneViewModel(
            _workspace, FolderTreeCommandHandler.Unconfined(_workspace), _tree, new FakeFilePlacesProvider());
        pane.Restore(snapshot: null, fallbackFolder: _root);
        return pane;
    }

    // ===== 一覧・並べ替え・絞り込み =====

    [Fact]
    public void 初期表示はプライマリフォルダーでフォルダーが先頭かつ名前は自然順()
    {
        var sut = CreateColumn();

        Assert.Equal(_root, sut.CurrentFolder);
        Assert.Equal(
            new[] { "docs", "src", "app.cs", "file2.txt", "file10.txt" },
            sut.Entries.Select(e => e.Name));
        Assert.Equal("2 フォルダー・3 ファイル", sut.StatusText);
    }

    [Fact]
    public void 更新日時で並べ替えると新しい順から始まる()
    {
        var sut = CreateColumn();

        sut.SortCommand.Execute("Modified");

        Assert.Equal(FilesSortColumn.Modified, sut.SortColumn);
        Assert.True(sut.SortDescending);   // 日時は「新しい方を見たい」ので降順から
        Assert.Equal(" ▼", sut.ModifiedSortMark);
        // フォルダーは並べ替え列に関わらず常に先頭（グループ内は同じ列で並ぶ）。
        Assert.Equal(2, sut.Entries.TakeWhile(e => e.IsDirectory).Count());
        Assert.Equal(
            new[] { "file10.txt", "file2.txt", "app.cs" },
            sut.Entries.Where(e => !e.IsDirectory).Select(e => e.Name));

        sut.SortCommand.Execute("Modified");   // 同じ列をもう一度 → 向きが反転
        Assert.False(sut.SortDescending);
        Assert.Equal("app.cs", sut.Entries.First(e => !e.IsDirectory).Name);   // 最も古いものが先頭
    }

    [Fact]
    public void サイズで並べ替えると大きい順から始まる()
    {
        var sut = CreateColumn();
        sut.SortCommand.Execute("Size");
        Assert.Equal("app.cs", sut.Entries.First(e => !e.IsDirectory).Name);
    }

    [Fact]
    public void 絞り込みは部分一致とワイルドカードに効く()
    {
        var sut = CreateColumn();

        sut.Filter = "file";
        Assert.Equal(new[] { "file2.txt", "file10.txt" }, sut.Entries.Select(e => e.Name));
        Assert.Contains("3 件を非表示", sut.StatusText);

        sut.Filter = "*.cs";
        Assert.Equal(new[] { "app.cs" }, sut.Entries.Select(e => e.Name));

        sut.Filter = "";
        Assert.Equal(5, sut.Entries.Count);
    }

    [Fact]
    public void 隠しファイルは既定で伏せられる()
    {
        var hidden = Path.Combine(_root, "secret.txt");
        File.WriteAllText(hidden, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var sut = CreateColumn();
        Assert.DoesNotContain(sut.Entries, e => e.Name == "secret.txt");

        sut.ShowHiddenFiles = true;
        Assert.Contains(sut.Entries, e => e.Name == "secret.txt");
    }

    [Fact]
    public void 更新しても同じ行インスタンスが残る()
    {
        var sut = CreateColumn();
        var before = sut.Entries.Single(e => e.Name == "file2.txt");

        File.WriteAllText(Path.Combine(_root, "file2.txt"), "22222");
        sut.RefreshCommand.Execute(null);

        var after = sut.Entries.Single(e => e.Name == "file2.txt");
        Assert.Same(before, after);           // 作り直すと選択とスクロール位置が飛ぶ
        Assert.Equal(5, after.Size);          // 値だけ更新される
    }

    // ===== ナビゲーション（ワークスペースの外も見られる） =====

    [Fact]
    public void 移動は戻る進む上へを覚える()
    {
        var sut = CreateColumn();
        Assert.False(sut.CanGoBack);

        sut.Navigate(_sub);
        Assert.Equal(_sub, sut.CurrentFolder);
        Assert.True(sut.CanGoBack);
        Assert.True(sut.CanGoUp);

        sut.GoBackCommand.Execute(null);
        Assert.Equal(_root, sut.CurrentFolder);
        Assert.True(sut.CanGoForward);

        sut.GoForwardCommand.Execute(null);
        Assert.Equal(_sub, sut.CurrentFolder);

        sut.GoUpCommand.Execute(null);
        Assert.Equal(_root, sut.CurrentFolder);
    }

    [Fact]
    public void ワークスペース外も開けて同じように操作できる()
    {
        // ワークスペース配下への限定はエージェントの手綱であって、人間のファイラの足枷ではない。
        var sut = CreateColumn();
        sut.Navigate(_outside);

        Assert.Equal(_outside, sut.CurrentFolder);
        Assert.Contains(sut.Entries, e => e.Name == "外部.txt");
        Assert.Equal(_outside, sut.TargetDirectory);
        Assert.Equal(_outside, sut.DropTargetFor(null));

        var created = sut.CreateEntry("新規.txt", isDirectory: false);
        Assert.True(File.Exists(created));

        var renamed = sut.RenameEntry(sut.Entries.Single(e => e.Name == "新規.txt"), "改名.txt");
        Assert.True(File.Exists(renamed));
        Assert.Equal(Path.Combine(_outside, "改名.txt"), renamed);
    }

    [Fact]
    public void ワークスペースの内と外はどちらへも受け渡しできる()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);

        // 外 → 中（取り込み）
        var imported = sut.PasteEntry(Path.Combine(_outside, "外部.txt"), move: false);
        Assert.Equal(Path.Combine(_sub, "外部.txt"), imported);
        Assert.True(File.Exists(imported));
        Assert.True(File.Exists(Path.Combine(_outside, "外部.txt")));   // コピー元は残る

        // 中 → 外（書き出し）
        sut.Navigate(_outside);
        var exported = sut.PasteEntry(Path.Combine(_root, "app.cs"), move: false);
        Assert.Equal(Path.Combine(_outside, "app.cs"), exported);
        Assert.True(File.Exists(exported));
    }

    [Fact]
    public void 上へはワークスペースフォルダーを越えて辿れる()
    {
        var sut = CreateColumn();
        Assert.True(sut.CanGoUp);

        sut.GoUpCommand.Execute(null);

        Assert.Equal(_base, sut.CurrentFolder);   // ワークスペースフォルダーの外へ出られる
    }

    [Fact]
    public void パンくずはワークスペース内なら所属フォルダー起点で外ならドライブから並ぶ()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);
        Assert.Equal(new[] { Path.GetFileName(_root), "src" }, sut.Breadcrumbs.Select(b => b.Name));
        Assert.True(sut.Breadcrumbs[^1].IsLast);

        sut.Navigate(_outside);
        Assert.True(sut.Breadcrumbs.Count > 1);
        Assert.Equal(Path.GetFileName(_outside), sut.Breadcrumbs[^1].Name);
        Assert.Equal(Path.GetPathRoot(_outside), sut.Breadcrumbs[0].Name);   // 先頭はドライブ
    }

    // ===== 場所（ピン留め共有・クイックアクセス） =====

    [Fact]
    public void 場所にはワークスペースとピン留めとクイックアクセスとPCが並ぶ()
    {
        var sut = CreateColumn();
        _tree.PinFolder(_sub);

        sut.LoadPlaces();

        Assert.Equal(
            new[] { "ワークスペース", "ピン留め", "クイックアクセス", "PC" },
            sut.Places.Select(g => g.Name));
        Assert.Equal(_root, sut.Places[0].Items.Single().FullPath);
        Assert.Equal(_sub, sut.Places[1].Items.Single().FullPath);
        Assert.Equal(FilesPlaceKind.QuickAccess, sut.Places[2].Items[0].Kind);
    }

    [Fact]
    public void 場所のワークスペースとピン留めはフルパスで並ぶ()
    {
        var sut = CreateColumn();
        _tree.PinFolder(_sub);

        sut.LoadPlaces();

        // フォルダー名や相対パスでは「どのドライブのどこか」が読めないので、行はフルパスそのもの。
        Assert.Equal(_root, sut.Places[0].Items.Single().Name);
        Assert.Equal(_sub, sut.Places[1].Items.Single().Name);
        // 同じものを二度書かないので、添えるパスは空。
        Assert.Equal("", sut.Places[1].Items.Single().DisplayPath);
        // Windows の呼び名で出るクイックアクセスとドライブだけ、所在地を添える。
        Assert.NotEqual("", sut.Places[2].Items[0].DisplayPath);
    }

    [Theory]
    [InlineData(@"C:\Projects\Loomo", @"C:\Projects\Loomo")]
    [InlineData(@"C:\Users\koya\source\repos\VeryLongProjectName\src\App\ViewModels",
                @"C:\…\VeryLongProjectName\src\App\ViewModels")]
    public void 場所のパスは長いときだけ中ほどを省く(string path, string expected)
        => Assert.Equal(expected, new FilesPlace("x", path, FilesPlaceKind.Pinned).DisplayPath);

    [Fact]
    public void ピン留めはツリーと共有される()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);

        Assert.True(sut.CanPin(_sub));
        sut.TogglePin(_sub);

        // ツリー側の一覧（＝保存されるピン）にそのまま出る。
        Assert.Contains(_sub, _tree.PinnedFolders);
        Assert.True(_tree.IsPinnedPath(_sub));
        Assert.True(sut.IsPinned(_sub));
        Assert.False(sut.CanPin(_sub));   // 二重にピンは留めない

        sut.TogglePin(_sub);
        Assert.DoesNotContain(_sub, _tree.PinnedFolders);
    }

    [Fact]
    public void ワークスペース外はピン留めできない()
    {
        var sut = CreateColumn();
        sut.Navigate(_outside);

        Assert.False(sut.CanPin(_outside));
        sut.TogglePin(_outside);

        Assert.DoesNotContain(_outside, _tree.PinnedFolders);
    }

    // ===== ファイル操作 =====

    [Fact]
    public void 新規作成は現在地に作られ名前の変更はタブ追従のために通知される()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);

        var created = sut.CreateEntry("memo.md", isDirectory: false);
        Assert.Equal(Path.Combine(_sub, "memo.md"), created);
        Assert.Contains(sut.Entries, e => e.Name == "memo.md");
        Assert.Equal(created, sut.PendingSelection);

        EntryRenamedEventArgs? renamed = null;
        sut.EntryRenamed += (_, e) => renamed = e;
        var entry = sut.Entries.Single(e => e.Name == "memo.md");
        var newPath = sut.RenameEntry(entry, "note.md");

        Assert.Equal(Path.Combine(_sub, "note.md"), newPath);
        Assert.NotNull(renamed);
        Assert.Equal(created, renamed!.Value.OldPath);
        Assert.Equal(newPath, renamed.Value.NewPath);
        Assert.Contains(sut.Entries, e => e.Name == "note.md");
    }

    [Fact]
    public void 貼り付けは同名を一意化して現在地へ複製する()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);

        var first = sut.PasteEntry(Path.Combine(_root, "app.cs"), move: false);
        var second = sut.PasteEntry(Path.Combine(_root, "app.cs"), move: false);

        Assert.Equal(Path.Combine(_sub, "app.cs"), first);
        Assert.NotEqual(first, second);                            // 上書きしない
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(Path.Combine(_root, "app.cs")));   // コピー元は残る
    }

    [Fact]
    public void 相対パスは所属するワークスペースフォルダー基準になる()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);
        var inner = sut.Entries.Single(e => e.Name == "inner.cs");
        Assert.Equal(Path.Combine("src", "inner.cs"), sut.RelativePathFor(inner));

        // ワークスペース外はフルパスのまま（相対にしても意味が無い）。
        sut.Navigate(_outside);
        var external = sut.Entries.Single(e => e.Name == "外部.txt");
        Assert.Equal(Path.Combine(_outside, "外部.txt"), sut.RelativePathFor(external));
    }

    // ===== カラム構成（1／2／4）と復元 =====

    [Fact]
    public void カラム数を変えても実体は使い回される()
    {
        var pane = CreatePane();
        Assert.Equal(1, pane.ColumnCount);
        Assert.Single(pane.Columns);
        Assert.Equal(FilesPaneViewModel.MaxColumns, pane.AllColumns.Count);

        pane.ColumnCount = 2;
        var second = pane.Columns[1];
        pane.ColumnCount = 1;
        Assert.Single(pane.Columns);

        pane.ColumnCount = 4;
        Assert.Equal(4, pane.Columns.Count);
        Assert.Same(second, pane.Columns[1]);   // VM は作り直さない（購読を貼り直さないため）
    }

    [Fact]
    public void カラムを増やすとワークスペースに登録したフォルダーを先に開く()
    {
        // 2カラム・4カラムを開く動機は「登録したフォルダーを並べて見る」なので、そこが最優先。
        _workspace.AddFolder(_outside);
        _tree.PinFolder(_sub);
        var pane = CreatePane();
        Assert.Equal(_root, pane.Columns[0].CurrentFolder);

        pane.ColumnCount = 2;
        Assert.Equal(_outside, pane.Columns[1].CurrentFolder);   // ①登録フォルダー

        pane.ColumnCount = 4;
        Assert.Equal(_sub, pane.Columns[2].CurrentFolder);       // ②ピン留め
        Assert.Equal(Path.Combine(_root, "docs"), pane.Columns[3].CurrentFolder);   // ③直下の逃げ道
    }

    [Fact]
    public void 登録フォルダーもピンも無ければ直下のフォルダーで代える()
    {
        var pane = CreatePane();

        pane.ColumnCount = 2;

        Assert.Equal(Path.Combine(_root, "docs"), pane.Columns[1].CurrentFolder);
    }

    [Fact]
    public void 登録フォルダーが出ていなければそこを開く()
    {
        var pane = CreatePane();
        pane.Columns[0].Navigate(_sub);   // 掘って入った状態

        pane.ColumnCount = 2;

        // ワークスペースフォルダー自身が画面から消えているので、まずそこを出す。
        Assert.Equal(_root, pane.Columns[1].CurrentFolder);
    }

    [Fact]
    public void 隠れていたカラムの現在地は覚えない()
    {
        var pane = CreatePane();
        pane.ColumnCount = 2;
        pane.Columns[1].Navigate(_outside);
        pane.ColumnCount = 1;

        pane.ColumnCount = 2;   // 出し直したら履歴ではなく今の「場所」から選び直す

        Assert.Equal(Path.Combine(_root, "docs"), pane.Columns[1].CurrentFolder);
    }

    [Fact]
    public void 操作対象のカラムは常に見えているものになる()
    {
        var pane = CreatePane();
        pane.ColumnCount = 2;
        pane.SetActiveColumn(pane.AllColumns[1]);
        Assert.True(pane.AllColumns[1].IsActive);
        Assert.False(pane.AllColumns[0].IsActive);

        pane.ColumnCount = 1;   // 隠れたカラムが操作対象のままだとキーの行き先が見えない場所になる

        Assert.Same(pane.AllColumns[0], pane.ActiveColumn);
        Assert.True(pane.AllColumns[0].IsActive);
    }

    [Fact]
    public void カラム構成と現在地は保存して戻せる()
    {
        var pane = CreatePane();
        pane.ColumnCount = 2;
        pane.Columns[0].Navigate(_sub);
        pane.Columns[1].Navigate(_outside);
        pane.Columns[0].SortCommand.Execute("Size");
        pane.SetActiveColumn(pane.AllColumns[1]);
        var snapshot = pane.Capture();

        var restored = new FilesPaneViewModel(
            _workspace, FolderTreeCommandHandler.Unconfined(_workspace), _tree, new FakeFilePlacesProvider());
        restored.Restore(snapshot, _root);

        Assert.Equal(2, restored.ColumnCount);
        Assert.Equal(_sub, restored.Columns[0].CurrentFolder);
        Assert.Equal(_outside, restored.Columns[1].CurrentFolder);   // 外の場所も復元する
        Assert.Equal(FilesSortColumn.Size, restored.Columns[0].SortColumn);
        Assert.Same(restored.AllColumns[1], restored.ActiveColumn);
        // 見えていないカラムは保存もしない（隠れている間の現在地は持たない）。
        Assert.Equal(2, snapshot.Columns.Count);
    }

    [Fact]
    public void 旧形式の1カラム保存も読める()
    {
        // 1カラムだけだった頃の workspaces.json（Columns を持たない）。
        var legacy = new FilesPaneSnapshot
        {
            CurrentFolder = _sub,
            SortColumn = FilesSortColumn.Modified,
            SortDescending = true,
        }.Migrate();

        var pane = CreatePane();
        pane.Restore(legacy, _root);

        Assert.Equal(1, pane.ColumnCount);
        Assert.Equal(_sub, pane.Columns[0].CurrentFolder);
        Assert.Equal(FilesSortColumn.Modified, pane.Columns[0].SortColumn);
    }

    [Fact]
    public void 保存されたフォルダーが消えていればプライマリへ倒す()
    {
        var pane = CreatePane();
        pane.Restore(
            new FilesPaneSnapshot
            {
                Columns = { new FilesColumnSnapshot { CurrentFolder = Path.Combine(_root, "消えたフォルダー") } },
            },
            _root);

        Assert.Equal(_root, pane.Columns[0].CurrentFolder);
    }

    [Fact]
    public void ツリーからの表示要求は操作対象のカラムで開く()
    {
        var pane = CreatePane();
        pane.ColumnCount = 2;
        pane.SetActiveColumn(pane.AllColumns[1]);

        pane.Reveal(Path.Combine(_sub, "inner.cs"));

        Assert.Equal(_root, pane.Columns[0].CurrentFolder);   // 左は動かない
        Assert.Equal(_sub, pane.Columns[1].CurrentFolder);
        Assert.Equal(Path.Combine(_sub, "inner.cs"), pane.Columns[1].PendingSelection);
    }
}

/// <summary>場所ポップアップ用のスタブ。実物（<see cref="WindowsFilePlacesProvider"/>）は
/// シェル COM を叩くので、テストでは固定値を返す。</summary>
internal sealed class FakeFilePlacesProvider : IFilePlacesProvider
{
    public IReadOnlyList<FilesPlace> QuickAccess() =>
        new[] { new FilesPlace("ダウンロード", Path.GetTempPath(), FilesPlaceKind.QuickAccess) };

    public IReadOnlyList<FilesPlace> Drives() =>
        new[] { new FilesPlace("C:", @"C:\", FilesPlaceKind.Drive) };
}
