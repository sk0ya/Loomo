using System.IO;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 軌跡（操作ログ）バーの記録ロジック（TrailViewModel）の検証。
/// 遷移の記録・直前と同一地点の非増殖・離脱位置の上書き・現在地の移動（スクラブ）・
/// 日付切替・SQLite 永続化との連携。
/// </summary>
public class TrailViewModelTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-trail.db");
    private readonly TrailStore _store;

    public TrailViewModelTests()
    {
        _store = new TrailStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private TrailViewModel CreateSut()
    {
        var sut = new TrailViewModel(_store);
        sut.EnsureLoaded();
        return sut;
    }

    [Fact]
    public void RecordFile_appends_entries_in_order_and_sets_current_to_latest()
    {
        var sut = CreateSut();

        sut.RecordFile(@"C:\work\a.cs", 10, 2);
        sut.RecordFile(@"C:\work\b.cs", 5, 0);

        Assert.Equal(2, sut.Entries.Count);
        Assert.True(sut.HasEntries);
        Assert.Equal(@"C:\work\a.cs", sut.Entries[0].Target);
        Assert.Equal("a.cs", sut.Entries[0].Label);
        Assert.Equal(1, sut.CurrentIndex);
        Assert.True(sut.Entries[1].IsCurrent);
        Assert.False(sut.Entries[0].IsCurrent);
    }

    [Fact]
    public void RecordFile_same_as_latest_updates_instead_of_appending()
    {
        var sut = CreateSut();

        sut.RecordFile(@"C:\work\a.cs", 10, 2);
        // タブ切替やフォーカス移動で同じファイルが再記録されても増殖しない（大文字小文字も同一視）。
        sut.RecordFile(@"C:\work\A.CS", 20, 4);

        var entry = Assert.Single(sut.Entries);
        Assert.Equal(20, entry.Line);
        Assert.Equal(4, entry.Column);
    }

    [Fact]
    public void Same_target_in_different_display_modes_is_preserved_and_reloaded()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", displayMode: DisplayMode.Layout);
        sut.RecordFile(@"C:\work\a.cs", displayMode: DisplayMode.Solo, stagePane: PaneKind.Editor);

        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal(DisplayMode.Layout, sut.Entries[0].Mode);
        Assert.Equal(DisplayMode.Solo, sut.Entries[1].Mode);
        Assert.Equal(PaneKind.Editor, sut.Entries[1].StagePane);

        var reloaded = new TrailViewModel(new TrailStore(_dbPath));
        reloaded.EnsureLoaded();
        Assert.Equal(DisplayMode.Solo, reloaded.Entries[1].Mode);
        Assert.Equal(PaneKind.Editor, reloaded.Entries[1].StagePane);
    }

    [Fact]
    public void Record_kinds_interleave_as_separate_entries()
    {
        var sut = CreateSut();

        sut.RecordPane("Terminal", "ターミナル");
        sut.RecordFile(@"C:\work\a.cs");
        sut.RecordPanel("Search", "検索");
        sut.RecordBrowser("https://example.com/", null);
        sut.RecordTerminal(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Terminal 1");

        Assert.Equal(5, sut.Entries.Count);
        Assert.Equal(TrailEntryKind.Pane, sut.Entries[0].Kind);
        Assert.Equal(TrailEntryKind.File, sut.Entries[1].Kind);
        Assert.Equal(TrailEntryKind.Panel, sut.Entries[2].Kind);
        Assert.Equal(TrailEntryKind.Browser, sut.Entries[3].Kind);
        Assert.Equal(TrailEntryKind.Terminal, sut.Entries[4].Kind);
        Assert.Equal("11111111-1111-1111-1111-111111111111", sut.Entries[4].Target);
    }

    [Fact]
    public void Pane_layout_is_part_of_each_log_entry_and_persists()
    {
        var sut = CreateSut();
        const string layoutA = """{"Orientation":"Columns","Children":[{"Kind":1},{"Kind":0}]}""";
        const string layoutB = """{"Orientation":"Rows","Children":[{"Kind":1},{"Kind":4}]}""";

        sut.RecordFile(@"C:\work\a.cs", paneLayout: layoutA);
        sut.RecordBrowser("https://example.com/", "Example", paneLayout: layoutB);

        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal(layoutA, sut.Entries[0].PaneLayout);

        // 再起動相当：各地点と一体の配置参照も復元される。
        var reloaded = new TrailViewModel(new TrailStore(_dbPath));
        reloaded.EnsureLoaded();
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal(layoutB, reloaded.Entries[1].PaneLayout);
    }

    [Fact]
    public void Deduped_point_updates_its_layout_context_without_adding_a_log()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", paneLayout: "layout-a");
        sut.RecordFile(@"C:\work\a.cs", paneLayout: "layout-b");

        var entry = Assert.Single(sut.Entries);
        Assert.Equal("layout-b", entry.PaneLayout);
        var reloaded = new TrailViewModel(new TrailStore(_dbPath));
        reloaded.EnsureLoaded();
        Assert.Equal("layout-b", Assert.Single(reloaded.Entries).PaneLayout);
    }

    [Fact]
    public void RecordBrowser_uses_title_or_host_and_updates_latest_label()
    {
        var sut = CreateSut();

        // NavigationCompleted 直後はタイトル未確定のことがある → ホスト名で仮表示。
        sut.RecordBrowser("https://example.com/docs", null);
        Assert.Equal("example.com", sut.Entries[0].Label);

        // 同じ URL の再記録はタイトルの後追い更新になる（エントリは増えない）。
        sut.RecordBrowser("https://example.com/docs", "Example Docs");
        var entry = Assert.Single(sut.Entries);
        Assert.Equal("Example Docs", entry.Label);
    }

    [Fact]
    public void UpdateLatestFilePosition_overwrites_position_of_latest_file_entry()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", 0, 0);

        // 離脱時のカーソル位置で上書き → 「戻る」が離れた場所になる。
        sut.UpdateLatestFilePosition(@"C:\work\a.cs", 120, 8);

        Assert.Equal(120, sut.Entries[0].Line);
        Assert.Equal(8, sut.Entries[0].Column);
        Assert.Contains(":121", sut.Entries[0].Tooltip);   // 表示は1始まり
    }

    [Fact]
    public void MoveCurrent_scrubs_within_bounds()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs");
        sut.RecordFile(@"C:\work\b.cs");
        sut.RecordFile(@"C:\work\c.cs");

        var back = sut.MoveCurrent(-1);
        Assert.Equal("b.cs", back!.Label);
        Assert.Equal(1, sut.CurrentIndex);

        // 端で止まる（下限を超えない・移動なしは null）
        Assert.NotNull(sut.MoveCurrent(-1));
        Assert.Null(sut.MoveCurrent(-1));
        Assert.Equal(0, sut.CurrentIndex);

        var fwd = sut.MoveCurrent(+1);
        Assert.Equal("b.cs", fwd!.Label);
    }

    [Fact]
    public void Entries_persist_and_reload_from_store()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", 3, 1);
        sut.RecordBrowser("https://example.com/", "Example");

        // 再起動相当：同じ DB から新しい VM を作ると今日の軌跡が復元される。
        var reloaded = new TrailViewModel(new TrailStore(_dbPath));
        reloaded.EnsureLoaded();

        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal(@"C:\work\a.cs", reloaded.Entries[0].Target);
        Assert.Equal(3, reloaded.Entries[0].Line);
        Assert.Equal(TrailEntryKind.Browser, reloaded.Entries[1].Kind);
    }

    [Fact]
    public void ShowDate_switches_to_past_day_and_back_to_today()
    {
        // 過去日のレコードを直接 DB へ用意する（VM 既定のスクラッチワークスペース=""）。
        var yesterday = DateTime.Now.AddDays(-1);
        _store.Append("", yesterday, (int)TrailEntryKind.File, @"C:\old\z.cs", "z.cs", 7, 0);

        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs");
        Assert.False(sut.IsViewingPast);

        sut.ShowDate(DateOnly.FromDateTime(yesterday));
        Assert.True(sut.IsViewingPast);
        var old = Assert.Single(sut.Entries);
        Assert.Equal("z.cs", old.Label);

        // 過去日を表示中の記録は表示へ出ない（今日へ積まれる）。
        sut.RecordFile(@"C:\work\b.cs");
        Assert.Single(sut.Entries);

        // × で今日へ戻ると、いま記録した分も含めて今日の軌跡が見える。
        sut.BackToTodayCommand.Execute(null);
        Assert.False(sut.IsViewingPast);
        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal("b.cs", sut.Entries[1].Label);
    }

    [Fact]
    public void SetWorkspace_separates_trails_per_workspace()
    {
        var sut = CreateSut();
        sut.SetWorkspace("ws-a");
        sut.RecordFile(@"C:\a\a.cs");
        sut.RecordFile(@"C:\a\a2.cs");

        // 別ワークスペースへ切替：a の軌跡は見えない。
        sut.SetWorkspace("ws-b");
        Assert.Empty(sut.Entries);
        sut.RecordFile(@"C:\b\b.cs");
        Assert.Single(sut.Entries);

        // 戻ると a の軌跡が丸ごと復元される（b の分は混ざらない）。
        sut.SetWorkspace("ws-a");
        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal("a2.cs", sut.Entries[1].Label);
        Assert.True(sut.HasEntries);
    }

    [Fact]
    public void Jump_raises_request_and_moves_current()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", 3, 1);
        sut.RecordFile(@"C:\work\b.cs");
        TrailEntryViewModel? requested = null;
        sut.JumpRequested += (_, e) => requested = e;

        sut.JumpCommand.Execute(sut.Entries[0]);

        Assert.Same(sut.Entries[0], requested);
        Assert.Equal(0, sut.CurrentIndex);
        Assert.True(sut.Entries[0].IsCurrent);
    }

    [Fact]
    public void Tooltip_contains_datetime_and_target()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", 3, 1);

        var tooltip = sut.Entries[0].Tooltip;
        Assert.Contains(@"C:\work\a.cs:4", tooltip);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), tooltip);
    }
}

/// <summary>軌跡の SQLite 永続化（TrailStore）の検証。日別の読み出しと上書き更新。</summary>
public class TrailStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-trailstore.db");
    private readonly TrailStore _store;

    public TrailStoreTests()
    {
        _store = new TrailStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Duplicate_layout_snapshots_are_normalized_to_one_row()
    {
        var now = DateTime.Now;
        const string layout = """{"Orientation":"Columns","Children":[{"Kind":1},{"Kind":0}]}""";
        _store.Append("ws", now, 0, "a", "a", -1, -1, paneLayout: layout);
        _store.Append("ws", now, 0, "b", "b", -1, -1, paneLayout: layout);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM trail_layouts;";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Updating_entry_removes_unreferenced_previous_layout()
    {
        var now = DateTime.Now;
        var id = _store.Append("ws", now, 0, "a", "a", -1, -1, paneLayout: "layout-a");
        _store.Update(id, now, "a", -1, -1, paneLayout: "layout-b");

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM trail_layouts ORDER BY id;";
        Assert.Equal("layout-b", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM trail_layouts;";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Append_and_LoadDay_split_by_local_date()
    {
        var today = DateTime.Now;
        var yesterday = today.AddDays(-1);
        _store.Append("ws", yesterday, 0, @"C:\old.cs", "old.cs", 1, 0);
        _store.Append("ws", today, 0, @"C:\new.cs", "new.cs", 2, 0);

        var todayList = _store.LoadDay("ws", DateOnly.FromDateTime(today));
        var yesterdayList = _store.LoadDay("ws", DateOnly.FromDateTime(yesterday));

        Assert.Equal("new.cs", Assert.Single(todayList).Label);
        Assert.Equal("old.cs", Assert.Single(yesterdayList).Label);
        Assert.Equal(2, _store.ListDays("ws").Count);
        Assert.True(_store.HasAny("ws"));
    }

    [Fact]
    public void LoadDay_filters_by_workspace()
    {
        var now = DateTime.Now;
        _store.Append("ws-a", now, 0, @"C:\a.cs", "a.cs", -1, -1);
        _store.Append("ws-b", now, 0, @"C:\b.cs", "b.cs", -1, -1);

        var day = DateOnly.FromDateTime(now);
        Assert.Equal("a.cs", Assert.Single(_store.LoadDay("ws-a", day)).Label);
        Assert.Equal("b.cs", Assert.Single(_store.LoadDay("ws-b", day)).Label);
        Assert.Empty(_store.LoadDay("ws-c", day));
        Assert.False(_store.HasAny("ws-c"));
    }

    [Fact]
    public void Update_overwrites_label_position_and_timestamp()
    {
        var t1 = DateTime.Now.AddMinutes(-5);
        var id = _store.Append("ws", t1, 1, "https://example.com/", "example.com", -1, -1);

        var t2 = DateTime.Now;
        _store.Update(id, t2, "Example Site", -1, -1, paneLayout: null);

        var record = Assert.Single(_store.LoadDay("ws", DateOnly.FromDateTime(t2)));
        Assert.Equal("Example Site", record.Label);
        Assert.Equal(t2.ToString("HH:mm:ss"), record.Timestamp.ToString("HH:mm:ss"));
    }

    [Fact]
    public void UpdatePosition_only_changes_line_and_column()
    {
        var now = DateTime.Now;
        var id = _store.Append("ws", now, 0, @"C:\a.cs", "a.cs", 1, 0);

        _store.UpdatePosition(id, 42, 7);

        var record = Assert.Single(_store.LoadDay("ws", DateOnly.FromDateTime(now)));
        Assert.Equal(42, record.Line);
        Assert.Equal(7, record.Column);
        Assert.Equal("a.cs", record.Label);
    }
}
