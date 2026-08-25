using System.IO;
using System.Linq;
using System.Windows.Data;
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
            new FolderTreeCommandHandler(_workspace, new FileOperationHistory()), new FolderTreeQuery());
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
            _workspace, FolderTreeCommandHandler.Unconfined(_workspace, new FileOperationHistory()), _tree, new FakeFilePlacesProvider());
        column.Restore(snapshot: null, fallbackFolder: _root);
        return column;
    }

    private FilesPaneViewModel CreatePane()
    {
        var pane = new FilesPaneViewModel(
            _workspace, FolderTreeCommandHandler.Unconfined(_workspace, new FileOperationHistory()), _tree, new FakeFilePlacesProvider());
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
    public void パンくずはワークスペース内でも常にドライブから並ぶ()
    {
        var sut = CreateColumn();
        sut.Navigate(_sub);
        // 住所欄なのでフルパスとして読めること優先。先頭はドライブ、末尾が現在地。
        Assert.Equal(Path.GetPathRoot(_sub), sut.Breadcrumbs[0].Name);
        Assert.Equal(new[] { Path.GetFileName(_root), "src" },
                     sut.Breadcrumbs.TakeLast(2).Select(b => b.Name));
        Assert.Equal(_sub, sut.Breadcrumbs[^1].FullPath);
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
    public void 場所は開いている間ピン留めの変化をその場で反映する()
    {
        // 常設パネルなので、項目を開いても畳まない＝開き直しでは更新されない。
        var sut = CreateColumn();
        sut.SetPlacesOpen(true);
        Assert.DoesNotContain(sut.Places, group => group.Name == "ピン留め");

        _tree.PinFolder(_sub);

        var pinned = Assert.Single(sut.Places, group => group.Name == "ピン留め");
        Assert.Equal(_sub, pinned.Items.Single().FullPath);

        // 中身が変わらない読み直しでは、グループの実体を作り替えない（スクロール位置を壊さないため）。
        var before = sut.Places.ToArray();
        sut.LoadPlaces();
        Assert.Equal(before.Length, sut.Places.Count);
        for (var i = 0; i < before.Length; i++)
            Assert.Same(before[i], sut.Places[i]);

        // 畳んだ後は追随しない（次に開いたときに読み直す）。
        sut.SetPlacesOpen(false);
        _tree.UnpinFolder(_sub);
        Assert.Contains(sut.Places, group => group.Name == "ピン留め");
        sut.SetPlacesOpen(true);
        Assert.DoesNotContain(sut.Places, group => group.Name == "ピン留め");
    }

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
    public void ペイン全体の履歴操作は操作対象カラムへ渡る()
    {
        var pane = CreatePane();
        pane.ColumnCount = 2;
        pane.Columns[0].Navigate(_sub);
        pane.Columns[1].Navigate(_outside);
        pane.SetActiveColumn(pane.Columns[1]);

        pane.NavigateHistory(back: true);

        Assert.Equal(Path.Combine(_root, "docs"), pane.Columns[1].CurrentFolder);
        Assert.Equal(_sub, pane.Columns[0].CurrentFolder);
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
            _workspace, FolderTreeCommandHandler.Unconfined(_workspace, new FileOperationHistory()), _tree, new FakeFilePlacesProvider());
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
    public void 表示形式は6種類から選べて変更通知が出る()
    {
        var sut = CreateColumn();
        var changes = 0;
        sut.StateChanged += (_, _) => changes++;

        Assert.Equal(
            new[] { FilesDisplayMode.Details, FilesDisplayMode.List, FilesDisplayMode.LargeIcons,
                FilesDisplayMode.MediumIcons, FilesDisplayMode.SmallIcons, FilesDisplayMode.Tiles },
            sut.DisplayModeOptions.Select(option => option.Value));

        foreach (var mode in sut.DisplayModeOptions.Select(option => option.Value))
            sut.DisplayMode = mode;

        Assert.Equal(FilesDisplayMode.Tiles, sut.DisplayMode);
        Assert.Equal(sut.DisplayModeOptions.Count - 1, changes);
    }

    [Fact]
    public void 表示形式はカラムのスナップショットへ保存して復元できる()
    {
        var sut = CreateColumn();
        sut.DisplayMode = FilesDisplayMode.MediumIcons;
        var snapshot = sut.Capture();

        var restored = CreateColumn();
        restored.Restore(snapshot, _root);

        Assert.Equal(FilesDisplayMode.MediumIcons, snapshot.DisplayMode);
        Assert.Equal(FilesDisplayMode.MediumIcons, restored.DisplayMode);
    }

    [Fact]
    public void 不正な表示形式は詳細へ戻る()
    {
        Assert.Equal(FilesDisplayMode.Details, FilesDisplayModes.Normalize((FilesDisplayMode)999));

        var sut = CreateColumn();
        sut.DisplayMode = FilesDisplayMode.Tiles;
        sut.Restore(new FilesColumnSnapshot { CurrentFolder = _root, DisplayMode = (FilesDisplayMode)999 }, _root);

        Assert.Equal(FilesDisplayMode.Details, sut.DisplayMode);

        sut.DisplayMode = (FilesDisplayMode)999;
        Assert.Equal(FilesDisplayMode.Details, sut.DisplayMode);
    }

    [Fact]
    public void 詳細列は幅表示順表示非表示を変更して保存できる()
    {
        var sut = CreateColumn();
        var size = sut.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Size);

        sut.SetColumnWidth(FilesColumnKey.Name, 360);
        size.IsVisible = false;
        sut.MoveColumnDownCommand.Execute(sut.ColumnSettings[0]);

        Assert.Equal(new[] { FilesColumnKey.Size, FilesColumnKey.Name, FilesColumnKey.Modified, FilesColumnKey.Type },
            sut.ColumnSettings.Select(setting => setting.Key));
        Assert.Equal(360, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.False(size.IsVisible);
        Assert.Equal(0, sut.SizeColumnIndex); // 非表示列は表示スロットを占有しない

        var snapshot = sut.Capture();
        var restored = CreateColumn();
        restored.Restore(snapshot, _root);

        Assert.Equal(sut.ColumnSettings.Select(setting => setting.Key),
            restored.ColumnSettings.Select(setting => setting.Key));
        Assert.Equal(360, restored.ColumnWidth(FilesColumnKey.Name));
        Assert.False(restored.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Size).IsVisible);
    }

    [Fact]
    public void 幅のドラッグ中は保存せず離した時に一度だけ書く()
    {
        // 掴んだまま動かしている間じゅう workspaces.json を書き直させない（1ドラッグ＝1回）。
        var sut = CreateColumn();
        var saves = 0;
        sut.StateChanged += (_, _) => saves++;

        sut.BeginColumnWidthDrag();
        sut.SetColumnWidth(FilesColumnKey.Name, 300);
        sut.SetColumnWidth(FilesColumnKey.Name, 320);
        sut.SetColumnWidth(FilesColumnKey.Name, 340);

        Assert.Equal(0, saves);
        Assert.Equal(340, sut.ColumnWidth(FilesColumnKey.Name));   // 見た目は動いている

        sut.EndColumnWidthDrag();

        Assert.Equal(1, saves);
        Assert.Equal(340, sut.Capture().FolderColumnSettings[_root]
            .Columns.Single(c => c.Key == FilesColumnKey.Name).Width);
    }

    [Fact]
    public void 幅は列ごとの下限と上限で止まる()
    {
        var sut = CreateColumn();

        sut.SetColumnWidth(FilesColumnKey.Name, 10);
        sut.SetColumnWidth(FilesColumnKey.Size, 10);
        sut.SetColumnWidth(FilesColumnKey.Type, 5000);

        Assert.Equal(120, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.Equal(40, sut.ColumnWidth(FilesColumnKey.Size));
        Assert.Equal(800, sut.ColumnWidth(FilesColumnKey.Type));
    }

    [Fact]
    public void 列を既定に戻すと幅も並びも表示も戻る()
    {
        var sut = CreateColumn();
        sut.SetColumnWidth(FilesColumnKey.Name, 360);
        sut.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Type).IsVisible = false;
        sut.MoveColumnDownCommand.Execute(sut.ColumnSettings[0]);

        sut.ResetColumnLayoutCommand.Execute(null);

        Assert.Equal(new[] { FilesColumnKey.Name, FilesColumnKey.Size, FilesColumnKey.Modified, FilesColumnKey.Type },
            sut.ColumnSettings.Select(setting => setting.Key));
        Assert.Equal(240, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.All(sut.ColumnSettings, setting => Assert.True(setting.IsVisible));
        // 既定に戻ったフォルダーは覚えない（覚えたままだと復元で同じ結果を書き戻すだけ太る）。
        Assert.Empty(sut.Capture().FolderColumnSettings);
    }

    [Fact]
    public void 詳細列設定はフォルダーごとに独立して復元される()
    {
        var sut = CreateColumn();
        sut.SetColumnWidth(FilesColumnKey.Name, 360);
        sut.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Type).IsVisible = false;

        sut.Navigate(_sub);
        Assert.Equal(240, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.True(sut.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Type).IsVisible);

        sut.SetColumnWidth(FilesColumnKey.Name, 180);
        sut.Navigate(_root);

        Assert.Equal(360, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.False(sut.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Type).IsVisible);
    }

    [Fact]
    public void 既定のままのフォルダーは列設定を溜め込まない()
    {
        // ワークスペースへ保存される辞書なので、ただ通り過ぎただけのフォルダーで太らせない。
        var sut = CreateColumn();
        for (var i = 0; i < 5; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_root, $"通過-{i}")).FullName;
            sut.Navigate(folder);
        }
        sut.Navigate(_root);
        sut.SetColumnWidth(FilesColumnKey.Name, 360);

        var snapshot = sut.Capture();

        var kept = Assert.Single(snapshot.FolderColumnSettings);
        Assert.Equal(_root, kept.Key);
        Assert.Equal(360, kept.Value.Columns.Single(c => c.Key == FilesColumnKey.Name).Width);
    }

    [Fact]
    public void フォルダーごとの列設定は上限を超えたら古い順に捨てる()
    {
        var sut = CreateColumn();
        var folders = new List<string>();
        for (var i = 0; i < 105; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_root, $"列-{i}")).FullName;
            folders.Add(folder);
            sut.Navigate(folder);
            sut.SetColumnWidth(FilesColumnKey.Name, 300 + i);   // 既定と違う＝覚える対象
        }

        var snapshot = sut.Capture();

        Assert.Equal(100, snapshot.FolderColumnSettings.Count);
        Assert.DoesNotContain(folders[0], snapshot.FolderColumnSettings.Keys);
        Assert.Contains(folders[^1], snapshot.FolderColumnSettings.Keys);
    }

    [Fact]
    public void 不正な列設定は既定値へ丸め名前列は必ず表示する()
    {
        var sut = CreateColumn();
        sut.Restore(new FilesColumnSnapshot
        {
            CurrentFolder = _root,
            ColumnSettings =
            [
                new() { Key = FilesColumnKey.Type, IsVisible = true, Width = 100 },
                new() { Key = (FilesColumnKey)999, IsVisible = true, Width = 100 },
                new() { Key = FilesColumnKey.Name, IsVisible = false, Width = 1 },
                new() { Key = FilesColumnKey.Size, IsVisible = true, Width = 1 },
                new() { Key = FilesColumnKey.Size, IsVisible = false, Width = 999 },
            ]
        }, _root);

        Assert.Equal(new[] { FilesColumnKey.Type, FilesColumnKey.Name, FilesColumnKey.Size, FilesColumnKey.Modified },
            sut.ColumnSettings.Select(setting => setting.Key));
        Assert.True(sut.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Name).IsVisible);
        Assert.Equal(120, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.Equal(40, sut.ColumnWidth(FilesColumnKey.Size));
    }

    [Fact]
    public void 列設定はワークスペースJSON往復で保持される()
    {
        var path = Path.Combine(_base, "workspaces-columns.json");
        var workspace = new WorkspaceSnapshot
        {
            RootPath = _root,
            Files = new FilesPaneSnapshot
            {
                Columns =
                [
                    new FilesColumnSnapshot
                    {
                        CurrentFolder = _root,
                        ColumnSettings =
                        [new() { Key = FilesColumnKey.Name, Width = 333 }],
                        FolderColumnSettings = new Dictionary<string, FilesColumnLayoutSnapshot>
                        {
                            [_root] = new() { Columns = [new() { Key = FilesColumnKey.Name, Width = 333 }] }
                        }
                    }
                ]
            }
        };
        var store = new WorkspaceStateStore(path);

        store.Save(new WorkspaceState { ActiveWorkspaceId = workspace.Id, Workspaces = [workspace] });

        var restored = store.LoadWorkspace(workspace.Id);
        Assert.Equal(333, restored?.Files?.Columns.Single().FolderColumnSettings[_root].Columns.Single().Width);
    }

    [Fact]
    public void 表示形式はワークスペース状態のJSON往復でも保持される()
    {
        var path = Path.Combine(_base, "workspaces.json");
        var workspace = new WorkspaceSnapshot
        {
            RootPath = _root,
            Files = new FilesPaneSnapshot
            {
                Columns =
                [
                    new FilesColumnSnapshot { CurrentFolder = _root, DisplayMode = FilesDisplayMode.Tiles }
                ]
            }
        };
        var store = new WorkspaceStateStore(path);

        store.Save(new WorkspaceState { ActiveWorkspaceId = workspace.Id, Workspaces = [workspace] });

        var restored = store.LoadWorkspace(workspace.Id);
        Assert.Equal(FilesDisplayMode.Tiles, restored?.Files?.Columns.Single().DisplayMode);
    }

    [Fact]
    public void グループ化は種類ごとに分かれグループ内は現在の列で並ぶ()
    {
        var sut = CreateColumn();
        sut.GroupBy = FilesGroupBy.Type;

        Assert.Equal(
            new[] { "docs", "src", "app.cs", "file2.txt", "file10.txt" },
            sut.Entries.Select(entry => entry.Name));
        Assert.Equal(FilesGroupBy.Type, sut.GroupBy);
        Assert.Equal(sut.Entries.Count, sut.EntriesView.Cast<FileEntryViewModel>().Count());
        Assert.All(sut.EntriesView.Cast<object>(), item => Assert.IsType<FileEntryViewModel>(item));

        var groups = sut.EntriesView.Groups!.Cast<CollectionViewGroup>().ToList();
        Assert.Equal(new[] { "folder", ".cs", ".txt" },
            groups.Select(group => ((FilesGroupValue)group.Name).Key));
        Assert.Equal(new[] { "docs", "src" },
            groups[0].Items.Cast<FileEntryViewModel>().Select(entry => entry.Name));
        Assert.Equal(new[] { "app.cs" },
            groups[1].Items.Cast<FileEntryViewModel>().Select(entry => entry.Name));
        Assert.Equal(new[] { "file2.txt", "file10.txt" },
            groups[2].Items.Cast<FileEntryViewModel>().Select(entry => entry.Name));
    }

    [Fact]
    public void グループ化の降順はグループ順だけを反転しグループ内ソートを保つ()
    {
        var sut = CreateColumn();
        sut.GroupBy = FilesGroupBy.Type;
        sut.SortCommand.Execute("Size");

        Assert.True(sut.SortDescending);
        Assert.Equal(new[] { "file2.txt", "file10.txt", "app.cs", "docs", "src" },
            sut.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void 更新日とサイズは空の一覧でもグループ状態を残さない()
    {
        var sut = CreateColumn();
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        sut.Navigate(empty);
        sut.GroupBy = FilesGroupBy.Modified;
        Assert.Empty(sut.Entries);
        Assert.Empty(sut.EntriesView.Groups!);

        sut.GroupBy = FilesGroupBy.Size;
        Assert.Empty(sut.EntriesView.Groups!);
    }

    [Fact]
    public void サイズグループは境界値を正しく分け昇降順を反映する()
    {
        var entries = new[]
        {
            new FileEntryViewModel(Path.Combine(_root, "zero.bin"), false, 0, new DateTime(2026, 1, 1)),
            new FileEntryViewModel(Path.Combine(_root, "byte.bin"), false, 1, new DateTime(2026, 1, 2)),
            new FileEntryViewModel(Path.Combine(_root, "kb.bin"), false, 1024, new DateTime(2026, 1, 3)),
            new FileEntryViewModel(Path.Combine(_root, "mb.bin"), false, 1024L * 1024, new DateTime(2026, 1, 4)),
            new FileEntryViewModel(Path.Combine(_root, "gb.bin"), false, 1024L * 1024 * 1024, new DateTime(2026, 1, 5)),
        };

        var ascending = FilesListing.Arrange(entries, FilesSortColumn.Name, false, "", false, FilesGroupBy.Size);
        Assert.Equal(new[] { "0", "small", "kb", "mb", "gb" },
            ascending.Select(entry => FilesListing.GroupValue(entry, FilesGroupBy.Size).Key));

        var descending = FilesListing.Arrange(entries, FilesSortColumn.Name, true, "", false, FilesGroupBy.Size);
        Assert.Equal(new[] { "gb", "mb", "kb", "small", "0" },
            descending.Select(entry => FilesListing.GroupValue(entry, FilesGroupBy.Size).Key));
    }

    [Fact]
    public void 単一グループと全表示形式でグループ項目と行型を維持する()
    {
        var only = Path.Combine(_root, "only");
        Directory.CreateDirectory(only);
        WriteFile(Path.Combine(only, "a.txt"), "a", new DateTime(2026, 1, 1));
        WriteFile(Path.Combine(only, "b.txt"), "b", new DateTime(2026, 1, 2));

        var sut = CreateColumn();
        sut.Navigate(only);
        sut.GroupBy = FilesGroupBy.Type;

        foreach (var mode in sut.DisplayModeOptions.Select(option => option.Value))
        {
            sut.DisplayMode = mode;
            Assert.Single(sut.EntriesView.Groups!);
            var group = Assert.IsAssignableFrom<CollectionViewGroup>(sut.EntriesView.Groups![0]);
            Assert.Equal(new[] { "a.txt", "b.txt" },
                group.Items.Cast<FileEntryViewModel>().Select(entry => entry.Name));
            Assert.All(sut.EntriesView.Cast<object>(), item => Assert.IsType<FileEntryViewModel>(item));
        }
    }

    [Fact]
    public void グループ化中もフォルダー単位の列レイアウト復元を壊さない()
    {
        var sut = CreateColumn();
        sut.GroupBy = FilesGroupBy.Type;
        sut.SetColumnWidth(FilesColumnKey.Name, 360);

        sut.Navigate(_sub);
        Assert.Equal(240, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.Equal(FilesGroupBy.Type, sut.GroupBy);

        sut.SetColumnWidth(FilesColumnKey.Name, 180);
        sut.Navigate(_root);

        Assert.Equal(360, sut.ColumnWidth(FilesColumnKey.Name));
        Assert.Equal(FilesGroupBy.Type, sut.GroupBy);
        Assert.Equal(new[] { "docs", "src", "app.cs", "file2.txt", "file10.txt" },
            sut.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void グループ化はカラムスナップショットとJSON往復で復元できる()
    {
        var sut = CreateColumn();
        sut.GroupBy = FilesGroupBy.Size;
        var snapshot = sut.Capture();

        var restored = CreateColumn();
        restored.Restore(snapshot, _root);
        Assert.Equal(FilesGroupBy.Size, restored.GroupBy);
        Assert.NotEmpty(restored.EntriesView.Groups!);

        var path = Path.Combine(_base, "workspaces-grouping.json");
        var workspace = new WorkspaceSnapshot
        {
            RootPath = _root,
            Files = new FilesPaneSnapshot
            {
                Columns = [new FilesColumnSnapshot { CurrentFolder = _root, GroupBy = FilesGroupBy.Modified }]
            }
        };
        var store = new WorkspaceStateStore(path);
        store.Save(new WorkspaceState { ActiveWorkspaceId = workspace.Id, Workspaces = [workspace] });

        Assert.Equal(FilesGroupBy.Modified,
            store.LoadWorkspace(workspace.Id)?.Files?.Columns.Single().GroupBy);
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
