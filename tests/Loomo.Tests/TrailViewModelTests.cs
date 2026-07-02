using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 軌跡（操作ログ）バーの記録ロジック（TrailViewModel）の検証。
/// 遷移の記録・直前と同一地点の非増殖・離脱位置の上書き・上限・クリア・ジャンプ要求。
/// </summary>
public class TrailViewModelTests
{
    [Fact]
    public void RecordFile_appends_entries_in_order()
    {
        var sut = new TrailViewModel();

        sut.RecordFile(@"C:\work\a.cs", 10, 2);
        sut.RecordFile(@"C:\work\b.cs", 5, 0);

        Assert.Equal(2, sut.Entries.Count);
        Assert.True(sut.HasEntries);
        Assert.Equal(@"C:\work\a.cs", sut.Entries[0].Target);
        Assert.Equal("a.cs", sut.Entries[0].Label);
        Assert.Equal("b.cs", sut.Entries[1].Label);
        Assert.Equal(TrailEntryKind.File, sut.Entries[0].Kind);
    }

    [Fact]
    public void RecordFile_same_as_latest_updates_instead_of_appending()
    {
        var sut = new TrailViewModel();

        sut.RecordFile(@"C:\work\a.cs", 10, 2);
        // タブ切替やフォーカス移動で同じファイルが再記録されても増殖しない（大文字小文字も同一視）。
        sut.RecordFile(@"C:\work\A.CS", 20, 4);

        var entry = Assert.Single(sut.Entries);
        Assert.Equal(20, entry.Line);
        Assert.Equal(4, entry.Column);
    }

    [Fact]
    public void RecordFile_without_position_keeps_previous_position()
    {
        var sut = new TrailViewModel();

        sut.RecordFile(@"C:\work\a.cs", 10, 2);
        // 未実体化タブの再活性化など、位置情報なし（-1）の再記録では既知の位置を消さない。
        sut.RecordFile(@"C:\work\a.cs");

        var entry = Assert.Single(sut.Entries);
        Assert.Equal(10, entry.Line);
        Assert.Equal(2, entry.Column);
    }

    [Fact]
    public void RecordFile_revisit_after_other_file_appends_new_entry()
    {
        var sut = new TrailViewModel();

        sut.RecordFile(@"C:\work\a.cs");
        sut.RecordFile(@"C:\work\b.cs");
        sut.RecordFile(@"C:\work\a.cs");

        // 別ファイルを挟んだ再訪は新しい通過として時系列に残る。
        Assert.Equal(3, sut.Entries.Count);
        Assert.Equal("a.cs", sut.Entries[2].Label);
    }

    [Fact]
    public void RecordBrowser_uses_title_or_host_and_updates_latest_label()
    {
        var sut = new TrailViewModel();

        // NavigationCompleted 直後はタイトル未確定のことがある → ホスト名で仮表示。
        sut.RecordBrowser("https://example.com/docs", null);
        Assert.Equal("example.com", sut.Entries[0].Label);

        // 同じ URL の再記録はタイトルの後追い更新になる（エントリは増えない）。
        sut.RecordBrowser("https://example.com/docs", "Example Docs");
        var entry = Assert.Single(sut.Entries);
        Assert.Equal("Example Docs", entry.Label);
        Assert.Equal(TrailEntryKind.Browser, entry.Kind);
    }

    [Fact]
    public void UpdateFilePosition_overwrites_position()
    {
        var sut = new TrailViewModel();
        sut.RecordFile(@"C:\work\a.cs", 0, 0);
        var entry = sut.Entries[0];

        // 離脱時のカーソル位置で上書き → 「戻る」が離れた場所になる。
        sut.UpdateFilePosition(entry, 120, 8);

        Assert.Equal(120, entry.Line);
        Assert.Equal(8, entry.Column);
        Assert.Equal("a.cs:121", entry.Display);   // 表示は1始まり
    }

    [Fact]
    public void Entries_are_capped_dropping_oldest()
    {
        var sut = new TrailViewModel();

        for (var i = 0; i < TrailViewModel.MaxEntries + 5; i++)
            sut.RecordFile($@"C:\work\f{i}.cs");

        Assert.Equal(TrailViewModel.MaxEntries, sut.Entries.Count);
        Assert.Equal("f5.cs", sut.Entries[0].Label);   // 古い5件が落ちる
    }

    [Fact]
    public void Clear_removes_all_and_hides_bar()
    {
        var sut = new TrailViewModel();
        sut.RecordFile(@"C:\work\a.cs");

        sut.ClearCommand.Execute(null);

        Assert.Empty(sut.Entries);
        Assert.False(sut.HasEntries);
    }

    [Fact]
    public void Jump_raises_request_with_entry()
    {
        var sut = new TrailViewModel();
        sut.RecordFile(@"C:\work\a.cs", 3, 1);
        TrailEntryViewModel? requested = null;
        sut.JumpRequested += (_, e) => requested = e;

        sut.JumpCommand.Execute(sut.Entries[0]);

        Assert.Same(sut.Entries[0], requested);
    }
}
