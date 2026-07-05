using System.IO;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.App.Layout;
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
    public void Latest_point_layout_can_be_updated_without_revisiting_or_changing_timestamp()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", paneLayout: "layout-a");
        var timestamp = sut.Entries[0].Timestamp;

        sut.UpdateLatestPaneLayout("layout-after-resize-or-move");

        var entry = Assert.Single(sut.Entries);
        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal("layout-after-resize-or-move", entry.PaneLayout);
        using var reloaded = new TrailStore(_dbPath);
        Assert.Equal("layout-after-resize-or-move",
            Assert.Single(reloaded.LoadDay("", DateOnly.FromDateTime(DateTime.Now))).PaneLayout);
    }

    public static TheoryData<TrailEntryKind> AllTrailKinds => new()
    {
        TrailEntryKind.File,
        TrailEntryKind.Browser,
        TrailEntryKind.Pane,
        TrailEntryKind.Panel,
        TrailEntryKind.Terminal,
        TrailEntryKind.Layout,
        TrailEntryKind.Preview,
        TrailEntryKind.Session,
        TrailEntryKind.Edit,
        TrailEntryKind.Git
    };

    [Theory]
    [MemberData(nameof(AllTrailKinds))]
    public void Latest_layout_update_covers_every_trail_kind_and_persists(TrailEntryKind kind)
    {
        var sut = CreateSut();
        sut.Record(kind, $"target-{kind}", $"label-{kind}", paneLayout: "before");

        sut.UpdateLatestPaneLayout("after");

        Assert.Equal("after", Assert.Single(sut.Entries).PaneLayout);
        using var reloaded = new TrailStore(_dbPath);
        Assert.Equal("after",
            Assert.Single(reloaded.LoadDay("", DateOnly.FromDateTime(DateTime.Now))).PaneLayout);
    }

    [Fact]
    public void Layout_change_is_an_independent_trail_point_and_persists()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", paneLayout: "layout-a");

        sut.RecordLayout("layout-key-b", "レイアウト変更", DisplayMode.Layout, null, "layout-b");

        Assert.Equal(2, sut.Entries.Count);
        var layout = sut.Entries[1];
        Assert.Equal(TrailEntryKind.Layout, layout.Kind);
        Assert.Equal("layout-b", layout.PaneLayout);
        var reloaded = new TrailViewModel(new TrailStore(_dbPath));
        reloaded.EnsureLoaded();
        Assert.Equal(TrailEntryKind.Layout, reloaded.Entries[1].Kind);
        Assert.Equal("layout-b", reloaded.Entries[1].PaneLayout);
    }

    [Fact]
    public void Repeated_same_layout_is_deduped_but_returning_to_it_after_another_layout_is_preserved()
    {
        var sut = CreateSut();

        sut.RecordLayout("a", "A", DisplayMode.Layout, null, "layout-a");
        sut.RecordLayout("a", "A", DisplayMode.Layout, null, "layout-a");
        sut.RecordLayout("b", "B", DisplayMode.Layout, null, "layout-b");
        sut.RecordLayout("a", "A", DisplayMode.Layout, null, "layout-a");

        Assert.Equal(new[] { "a", "b", "a" }, sut.Entries.Select(e => e.Target));
    }

    [Fact]
    public void Workspace_switch_must_commit_outgoing_layout_before_changing_trail_workspace()
    {
        var sut = CreateSut();
        sut.SetWorkspace("ws-a");
        sut.RecordFile(@"C:\a.cs", paneLayout: "a-before-leaving");
        sut.SetWorkspace("ws-b");
        sut.RecordFile(@"C:\b.cs", paneLayout: "b-layout");

        // ShellWindow.SwitchWorkspaceAsync の必須順序を再現する。
        sut.SetWorkspace("ws-a");
        sut.UpdateLatestPaneLayout("a-at-switch");
        sut.SetWorkspace("ws-b");

        Assert.Equal("b-layout", Assert.Single(sut.Entries).PaneLayout);
        using var verify = new TrailStore(_dbPath);
        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal("a-at-switch", Assert.Single(verify.LoadDay("ws-a", today)).PaneLayout);
        Assert.Equal("b-layout", Assert.Single(verify.LoadDay("ws-b", today)).PaneLayout);
    }

    [Fact]
    public void Updating_layout_while_viewing_past_updates_todays_latest_point_only()
    {
        var yesterday = DateTime.Now.AddDays(-1);
        _store.Append("", yesterday, (int)TrailEntryKind.File, @"C:\old.cs", "old.cs", -1, -1,
            paneLayout: "old-layout");
        var sut = CreateSut();
        sut.RecordFile(@"C:\today.cs", paneLayout: "today-before");
        sut.ShowDate(DateOnly.FromDateTime(yesterday));

        sut.UpdateLatestPaneLayout("today-after");

        Assert.Equal("old-layout", Assert.Single(sut.Entries).PaneLayout);
        sut.BackToTodayCommand.Execute(null);
        Assert.Equal("today-after", Assert.Single(sut.Entries).PaneLayout);
    }

    [Fact]
    public void Updating_layout_with_no_point_is_a_no_op()
    {
        var sut = CreateSut();

        sut.UpdateLatestPaneLayout("layout");

        Assert.Empty(sut.Entries);
        Assert.Empty(_store.LoadDay("", DateOnly.FromDateTime(DateTime.Now)));
    }

    [Fact]
    public void Latest_layout_can_be_cleared_and_old_snapshot_is_cleaned_up()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\a.cs", paneLayout: "layout");

        sut.UpdateLatestPaneLayout(null);

        Assert.Null(Assert.Single(sut.Entries).PaneLayout);
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM trail_layouts;";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
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
    public void RecordPreview_records_source_file_as_target_and_dedupes_case_insensitively()
    {
        var sut = CreateSut();

        // プレビュー地点は「ペイン」ではなく映していたファイルを戻り先にする。
        sut.RecordPreview(@"C:\work\readme.md");
        var entry = Assert.Single(sut.Entries);
        Assert.Equal(TrailEntryKind.Preview, entry.Kind);
        Assert.Equal(@"C:\work\readme.md", entry.Target);
        Assert.Equal("readme.md", entry.Label);
        Assert.Contains(@"C:\work\readme.md", entry.Tooltip);

        // 同じファイルのプレビューへ戻っても（大文字小文字違いでも）増殖しない。
        sut.RecordPreview(@"C:\work\README.MD");
        Assert.Single(sut.Entries);

        // 別ファイルのプレビューは別の地点。
        sut.RecordPreview(@"C:\work\notes.md");
        Assert.Equal(2, sut.Entries.Count);
    }

    [Fact]
    public void Preview_and_file_of_the_same_path_are_distinct_points()
    {
        var sut = CreateSut();

        // 同じファイルでも「エディタで開いた地点」と「プレビューを見に行った地点」は別物として残る。
        sut.RecordFile(@"C:\work\a.md");
        sut.RecordPreview(@"C:\work\a.md");

        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal(TrailEntryKind.File, sut.Entries[0].Kind);
        Assert.Equal(TrailEntryKind.Preview, sut.Entries[1].Kind);
    }

    [Fact]
    public void RecordSession_records_id_as_target_and_dedupes_by_id()
    {
        var sut = CreateSut();

        // AI セッションは ID を戻り先にし、タイトルをラベルにする。
        sut.RecordSession("session-1", "起動不具合の調査");
        var entry = Assert.Single(sut.Entries);
        Assert.Equal(TrailEntryKind.Session, entry.Kind);
        Assert.Equal("session-1", entry.Target);
        Assert.Equal("起動不具合の調査", entry.Label);

        // 同じセッションの再アクティブ化はタイトルだけ後追い更新（増殖しない）。
        sut.RecordSession("session-1", "起動不具合の調査（続き）");
        Assert.Equal("起動不具合の調査（続き）", Assert.Single(sut.Entries).Label);

        // 別のセッションは別の地点。タイトル未確定なら既定ラベル。
        sut.RecordSession("session-2", "");
        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal("セッション", sut.Entries[1].Label);
    }

    [Fact]
    public void RecordEdit_stacks_per_line_dedupes_same_line_and_is_distinct_from_file()
    {
        var sut = CreateSut();

        // エディタで開いた地点（File）と編集した地点（Edit）は別物として残る。
        sut.RecordFile(@"C:\work\a.cs", 3, 1);
        sut.RecordEdit(@"C:\work\a.cs", 10, 2);
        Assert.Equal(2, sut.Entries.Count);
        Assert.Equal(TrailEntryKind.Edit, sut.Entries[1].Kind);
        Assert.Equal(@"C:\work\a.cs", sut.Entries[1].Target);
        Assert.Equal("a.cs", sut.Entries[1].Label);
        Assert.Equal(10, sut.Entries[1].Line);
        Assert.Contains(@"C:\work\a.cs:11", sut.Entries[1].Tooltip);   // 表示は1始まり

        // 同じ行を編集し続ける間は増やさない（大文字小文字も同一視・列違いは無視）。
        sut.RecordEdit(@"C:\work\A.CS", 10, 8);
        Assert.Equal(2, sut.Entries.Count);

        // 同じファイルでも別の行を編集したら新しい点を積む（要件）。
        sut.RecordEdit(@"C:\work\a.cs", 25, 0);
        Assert.Equal(3, sut.Entries.Count);
        Assert.Equal(25, sut.Entries[2].Line);

        // 別ファイルの編集も別の地点。
        sut.RecordEdit(@"C:\work\b.cs", 1, 0);
        Assert.Equal(4, sut.Entries.Count);
        Assert.Contains("軌跡、編集、b.cs、2行", sut.Entries[3].AccessibleName);
    }

    [Fact]
    public void RecordGit_logs_operation_with_key_target_and_dedupes_consecutive_same_key()
    {
        var sut = CreateSut();

        sut.RecordGit("commit", "コミット");
        var entry = Assert.Single(sut.Entries);
        Assert.Equal(TrailEntryKind.Git, entry.Kind);
        Assert.Equal("commit", entry.Target);
        Assert.Equal("コミット", entry.Label);
        Assert.Null(entry.PaneLayout);   // ログ専用：復元しないので配置は載せない

        // 連続する同種操作（多段の破棄＝clean+restore を1点にまとめる等）はデデュープで畳む。
        sut.RecordGit("commit", "コミット（amend）");
        Assert.Equal("コミット（amend）", Assert.Single(sut.Entries).Label);

        // 別種の操作は別の地点。
        sut.RecordGit("push", "プッシュ");
        Assert.Equal(2, sut.Entries.Count);
        Assert.Contains("軌跡、Git、プッシュ", sut.Entries[1].AccessibleName);
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
    public void MoveToLatest_selects_the_newest_point()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs");
        sut.RecordFile(@"C:\work\b.cs");
        sut.RecordFile(@"C:\work\c.cs");
        sut.MoveCurrent(-2);                 // 過去（a.cs）へスクラブ
        Assert.Equal(0, sut.CurrentIndex);

        var latest = sut.MoveToLatest();     // 「今」へ戻す
        Assert.Equal("c.cs", latest!.Label);
        Assert.Equal(2, sut.CurrentIndex);
        Assert.True(latest.IsCurrent);
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

    [Fact]
    public void AccessibleName_identifies_entry_and_current_state()
    {
        var sut = CreateSut();
        sut.RecordFile(@"C:\work\a.cs", 3, 1);

        var name = sut.Entries[0].AccessibleName;
        Assert.Contains("軌跡、ファイル、a.cs、4行", name);
        Assert.Contains("現在地", name);

        sut.RecordPanel("Search", "検索");
        Assert.DoesNotContain("現在地", sut.Entries[0].AccessibleName);
        Assert.Contains("軌跡、パネル、検索", sut.Entries[1].AccessibleName);
    }

    [Fact]
    public void Every_kind_has_a_distinct_glyph()
    {
        // §27.11-D：Pane と Layout が同じ記号だと、バー上で種別を見分けられない。
        var sut = CreateSut();
        var glyphs = new List<string>();
        foreach (var kind in Enum.GetValues<TrailEntryKind>())
        {
            sut.Record(kind, $"target-{kind}", $"label-{kind}");
            glyphs.Add(sut.Entries[^1].Glyph);
        }

        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
    }

    [Fact]
    public void HourLabel_shows_live_clock_at_latest_point_and_band_when_scrubbed_back()
    {
        // §27.7.2：ライブ地点（軌跡の最新＝「今」）では現在時刻 HH:mm、過去地点へ戻ると HH:00。
        var clock = new DateTime(2026, 7, 3, 9, 15, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();

        sut.RecordFile(@"C:\work\a.cs");            // 09:15
        Assert.Equal("09:15", sut.HourLabel);

        clock = new DateTime(2026, 7, 3, 10, 40, 0);
        sut.RecordFile(@"C:\work\b.cs");            // 10:40（最新＝ライブ）
        Assert.Equal("10:40", sut.HourLabel);

        // 時計だけ進めてもライブ地点なら現在時刻に追従する（新しい記録は無い）。
        clock = new DateTime(2026, 7, 3, 10, 52, 0);
        sut.RefreshHourLabel();
        Assert.Equal("10:52", sut.HourLabel);

        // 過去のドットへスクラブすると、その地点の時間帯を HH:00（＝HH:00〜HH:59）で表す。
        sut.MoveCurrent(-1);                        // 09:15 の地点へ
        Assert.Equal("09:00", sut.HourLabel);
    }

    [Fact]
    public void HourLabel_shows_band_when_viewing_a_past_day()
    {
        var clock = new DateTime(2026, 7, 3, 9, 15, 0);
        _store.Append("", new DateTime(2026, 7, 1, 14, 30, 0),
            (int)TrailEntryKind.File, @"C:\old.cs", "old.cs", -1, -1);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\a.cs");

        sut.ShowDate(new DateOnly(2026, 7, 1));

        // 過去日表示はライブではない：最後の地点の時間帯を HH:00 で出す。
        Assert.True(sut.IsViewingPast);
        Assert.Equal("14:00", sut.HourLabel);
    }

    [Fact]
    public void Hours_lists_distinct_bands_ascending_for_the_displayed_day()
    {
        var clock = new DateTime(2026, 7, 3, 9, 5, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();

        sut.RecordFile(@"C:\work\a.cs");                        // 09
        clock = new DateTime(2026, 7, 3, 9, 40, 0);
        sut.RecordFile(@"C:\work\b.cs");                        // 09（同じ時間帯）
        clock = new DateTime(2026, 7, 3, 11, 10, 0);
        sut.RecordFile(@"C:\work\c.cs");                        // 11

        Assert.Equal(new[] { 9, 11 }, sut.Hours.Select(h => h.Hour));
        Assert.Equal(new[] { "09:00", "11:00" }, sut.Hours.Select(h => h.Label));
    }

    [Fact]
    public void SelectHour_moves_current_to_the_first_dot_of_that_band_without_jumping()
    {
        var clock = new DateTime(2026, 7, 3, 9, 5, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\a.cs");                        // 0: 09
        clock = new DateTime(2026, 7, 3, 11, 10, 0);
        sut.RecordFile(@"C:\work\c.cs");                        // 1: 11
        clock = new DateTime(2026, 7, 3, 11, 45, 0);
        sut.RecordFile(@"C:\work\d.cs");                        // 2: 11

        var jumped = false;
        sut.JumpRequested += (_, _) => jumped = true;
        sut.SelectHour(sut.Hours.Single(h => h.Hour == 11));

        Assert.Equal(1, sut.CurrentIndex);                     // その時間帯の先頭ドット
        Assert.False(jumped);                                  // バー内ナビゲーションのみ（画面復元はしない）
    }

    [Fact]
    public void StartsNewHour_marks_the_first_dot_of_each_band()
    {
        // ドット列の時間帯境界に区切り（|）を出すためのフラグ（§27.7.3）。
        var clock = new DateTime(2026, 7, 3, 9, 5, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\a.cs");                        // 09
        clock = new DateTime(2026, 7, 3, 9, 40, 0);
        sut.RecordFile(@"C:\work\b.cs");                        // 09（同帯）
        clock = new DateTime(2026, 7, 3, 10, 2, 0);
        sut.RecordFile(@"C:\work\c.cs");                        // 10（新帯）

        Assert.False(sut.Entries[0].StartsNewHour);            // 先頭は常に false
        Assert.False(sut.Entries[1].StartsNewHour);            // 同じ時間帯 → 区切りなし
        Assert.True(sut.Entries[2].StartsNewHour);             // 時間帯が変わる → 区切りあり

        // 区切りには時間帯の開始時刻ラベル（HH:00）を一緒に出す（線だけだと見えないため）。
        Assert.Equal("10:00", sut.Entries[2].HourBandLabel);
        Assert.Equal("09:00", sut.Entries[0].HourBandLabel);
    }

    [Fact]
    public void Crossing_midnight_while_following_rolls_display_to_the_new_day()
    {
        // §27.11-C：実行したまま日付を跨ぐと、表示が前日に張り付いて新しい記録が見えなくなっていた。
        var clock = new DateTime(2026, 7, 3, 23, 59, 0);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\day1.cs");
        Assert.Single(sut.Entries);

        clock = new DateTime(2026, 7, 4, 0, 1, 0);   // 日付を跨ぐ
        sut.RecordFile(@"C:\work\day2.cs");

        // 表示は新しい今日へ繰り上がり、前日分は混ざらず、過去表示扱いにもならない。
        var entry = Assert.Single(sut.Entries);
        Assert.Equal("day2.cs", entry.Label);
        Assert.False(sut.IsViewingPast);

        // 前日・当日はそれぞれ別の日として永続化されている。
        Assert.Equal("day1.cs",
            Assert.Single(_store.LoadDay("", new DateOnly(2026, 7, 3))).Label);
        Assert.Equal("day2.cs",
            Assert.Single(_store.LoadDay("", new DateOnly(2026, 7, 4))).Label);
    }

    [Fact]
    public void Crossing_midnight_while_viewing_past_does_not_disturb_the_past_view()
    {
        var clock = new DateTime(2026, 7, 3, 23, 59, 0);
        _store.Append("", new DateTime(2026, 7, 1, 10, 0, 0),
            (int)TrailEntryKind.File, @"C:\old.cs", "old.cs", -1, -1);
        var sut = new TrailViewModel(_store, () => clock);
        sut.EnsureLoaded();
        sut.RecordFile(@"C:\work\day1.cs");

        sut.ShowDate(new DateOnly(2026, 7, 1));
        Assert.True(sut.IsViewingPast);
        Assert.Equal("old.cs", Assert.Single(sut.Entries).Label);

        clock = new DateTime(2026, 7, 4, 0, 1, 0);   // 過去日を見ている最中に日付を跨ぐ
        sut.RecordFile(@"C:\work\day2.cs");

        // 過去表示は乱れない。
        Assert.True(sut.IsViewingPast);
        Assert.Equal("old.cs", Assert.Single(sut.Entries).Label);

        // 今日へ戻ると、新しい今日（7/4）に day2 だけが見える（day1 は前日 7/3）。
        sut.BackToTodayCommand.Execute(null);
        Assert.False(sut.IsViewingPast);
        Assert.Equal("day2.cs", Assert.Single(sut.Entries).Label);
    }
}

/// <summary>ペイン配置の構造署名（<see cref="PaneLayoutTree.StructureSignature"/>）の検証。
/// 軌跡のレイアウト変更検出で、リサイズ（比率だけの変化）を新しい地点にしないための土台。</summary>
public class PaneLayoutStructureSignatureTests
{
    private static PaneNodeSnapshot Leaf(PaneKind kind, double weight, bool hidden = false)
        => new() { Kind = kind, Weight = weight, Hidden = hidden };

    private static PaneNodeSnapshot Split(string orientation, params PaneNodeSnapshot[] children)
        => new() { Orientation = orientation, Children = children.ToList() };

    [Fact]
    public void Signature_ignores_weight_but_reflects_structure()
    {
        var layout = Split("Columns", Leaf(PaneKind.Editor, 0.3), Leaf(PaneKind.Terminal, 0.7));
        var resized = Split("Columns", Leaf(PaneKind.Editor, 0.8), Leaf(PaneKind.Terminal, 0.2));
        var restructured = Split("Rows", Leaf(PaneKind.Editor, 0.5), Leaf(PaneKind.Terminal, 0.5));

        // §27.11-B：リサイズ（比率だけの差）は同じ署名＝レイアウトドットを増やさない。
        Assert.Equal(PaneLayoutTree.StructureSignature(layout),
            PaneLayoutTree.StructureSignature(resized));
        // 行列の向きが変われば別構造。
        Assert.NotEqual(PaneLayoutTree.StructureSignature(layout),
            PaneLayoutTree.StructureSignature(restructured));
    }

    [Fact]
    public void Signature_distinguishes_hidden_and_pane_kind()
    {
        var visible = Split("Columns", Leaf(PaneKind.Editor, 1), Leaf(PaneKind.Terminal, 1));
        var hidden = Split("Columns", Leaf(PaneKind.Editor, 1), Leaf(PaneKind.Terminal, 1, hidden: true));
        var otherPane = Split("Columns", Leaf(PaneKind.Editor, 1), Leaf(PaneKind.Browser, 1));

        Assert.NotEqual(PaneLayoutTree.StructureSignature(visible),
            PaneLayoutTree.StructureSignature(hidden));
        Assert.NotEqual(PaneLayoutTree.StructureSignature(visible),
            PaneLayoutTree.StructureSignature(otherPane));
    }

    [Fact]
    public void Signature_matches_snapshots_equivalence()
    {
        var a = Split("Columns", Leaf(PaneKind.Editor, 0.4), Leaf(PaneKind.Terminal, 0.6));
        var b = Split("Columns", Leaf(PaneKind.Editor, 0.9), Leaf(PaneKind.Terminal, 0.1));

        // 署名一致 ⇔ SnapshotsEquivalent（比率無視の同一性）と整合する。
        Assert.True(PaneLayoutTree.SnapshotsEquivalent(a, b));
        Assert.Equal(PaneLayoutTree.StructureSignature(a), PaneLayoutTree.StructureSignature(b));
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
    public void Version_zero_database_is_migrated_without_losing_entries()
    {
        _store.Dispose();
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE trail_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace TEXT NOT NULL DEFAULT '', day TEXT NOT NULL, timestamp TEXT NOT NULL,
                    kind INTEGER NOT NULL, target TEXT NOT NULL, label TEXT NOT NULL,
                    line INTEGER NOT NULL DEFAULT -1, col INTEGER NOT NULL DEFAULT -1
                );
                INSERT INTO trail_entries(workspace, day, timestamp, kind, target, label, line, col)
                VALUES ('ws', $day, $timestamp, 0, 'C:\old.cs', 'old.cs', 12, 3);
                """;
            var now = DateTime.Now;
            cmd.Parameters.AddWithValue("$day", now.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$timestamp", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.ExecuteNonQuery();
        }

        using var migrated = new TrailStore(_dbPath);
        var entry = Assert.Single(migrated.LoadDay("ws", DateOnly.FromDateTime(DateTime.Now)));
        Assert.Equal("old.cs", entry.Label);
        Assert.Equal(DisplayMode.Layout, entry.DisplayMode);

        using var verify = new SqliteConnection($"Data Source={_dbPath}");
        verify.Open();
        using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, (long)version.ExecuteScalar()!);
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

    [Fact]
    public void UpdatePaneLayout_only_changes_layout_and_cleans_old_snapshot()
    {
        var now = DateTime.Now;
        var id = _store.Append("ws", now, 0, "a", "a", 4, 2, paneLayout: "layout-a");

        _store.UpdatePaneLayout(id, "layout-b");

        var record = Assert.Single(_store.LoadDay("ws", DateOnly.FromDateTime(now)));
        Assert.Equal("layout-b", record.PaneLayout);
        Assert.Equal(4, record.Line);
        Assert.Equal(now.ToString("HH:mm:ss"), record.Timestamp.ToString("HH:mm:ss"));
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM trail_layouts;";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }
}
