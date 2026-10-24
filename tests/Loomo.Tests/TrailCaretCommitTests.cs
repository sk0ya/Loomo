using System.IO;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 同じファイルの中の遠い移動を軌跡の点にする判定（<see cref="TrailCaretCommitController"/>、§27.3.3）。
/// File の点はタブの活性化でしか積まれず、同じファイルの中の定義ジャンプや検索は一つも残らなかった。
/// とどまったら 1 点にするタイマーは配線側の話なので、ここでは「どこに立っているか」「離れた場所を点にするか」を見る。
/// </summary>
public sealed class TrailCaretCommitTests : IDisposable
{
    private const string A = @"C:\work\a.cs";
    private const string B = @"C:\work\b.cs";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-trail-caret.db");
    private readonly TrailStore _store;
    private readonly Dictionary<string, TrailFilePosition> _carets = new(StringComparer.OrdinalIgnoreCase);

    public TrailCaretCommitTests()
    {
        _store = new TrailStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Moving_far_within_the_same_file_is_away_but_nearby_or_other_files_are_not()
    {
        var (trail, sut) = Create();
        trail.RecordFile(A, 20, 0);

        Assert.False(sut.IsAwayFromAnchor(new(A, 29, 0)));    // 10 行未満は同じ場所（打鍵・矢印移動）
        Assert.True(sut.IsAwayFromAnchor(new(A, 30, 0)));
        Assert.True(sut.IsAwayFromAnchor(new(@"C:\WORK\A.CS", 500, 0)));   // パスの大小は無視
        Assert.False(sut.IsAwayFromAnchor(new(B, 500, 0)));   // 別ファイルはタブの活性化が記録する
    }

    [Fact]
    public void Leaving_from_far_away_returns_that_place_and_leaving_nearby_returns_nothing()
    {
        var (trail, sut) = Create();
        trail.RecordFile(A, 20, 0);

        // 近くで離れた：離脱位置の上書き（従来どおり）に任せる。
        _carets[A] = new(A, 25, 3);
        Assert.Null(sut.TakeDeparture(arriving: null));

        // G で末尾へ飛んで、とどまる前に別のファイルへ：離れた場所を別の点にする。
        _carets[A] = new(A, 480, 0);
        Assert.Equal(new TrailFilePosition(A, 480, 0), sut.TakeDeparture(arriving: new(B, 0, 0)));
    }

    [Fact]
    public void Leaving_to_record_an_edit_at_the_same_place_does_not_add_a_file_point()
    {
        var (trail, sut) = Create();
        trail.RecordFile(A, 20, 0);
        _carets[A] = new(A, 300, 0);

        // 遠くへ移ってすぐ打ち始めた：編集の点がその場所を代表するので File の点は二重に積まない。
        Assert.Null(sut.TakeDeparture(arriving: new(A, 302, 0)));
    }

    [Fact]
    public void After_an_edit_point_moving_far_is_away_from_the_edit()
    {
        var (trail, sut) = Create();
        trail.RecordFile(A, 3, 0);
        trail.RecordEdit(A, 40, 0);

        // 最新が編集の点なら、編集した場所が立っている場所（編集の後に歩いた先が残らなかった）。
        Assert.False(sut.IsAwayFromAnchor(new(A, 45, 0)));
        _carets[A] = new(A, 200, 0);
        Assert.Equal(200, sut.TakeDeparture(arriving: null)?.Line);
    }

    [Fact]
    public void Landing_on_a_past_point_measures_from_there_until_the_next_record()
    {
        var (trail, sut) = Create();
        trail.RecordFile(A, 500, 0);

        // 軌跡で過去の A:20 へ戻った：最新の点（A:500）ではなく着地点から測る。
        sut.Land(new(A, 20, 0));
        Assert.False(sut.IsAwayFromAnchor(new(A, 22, 0)));
        Assert.True(sut.IsAwayFromAnchor(new(A, 200, 0)));

        // 新しい点が積まれたら、最新の点へ戻る。
        trail.RecordFile(B, 0, 0);
        sut.Recorded();
        Assert.Equal(B, sut.Anchor?.Path);
        Assert.False(sut.IsAwayFromAnchor(new(A, 200, 0)));   // A の移動はもう測らない（A へ戻るのはタブの活性化）
    }

    [Fact]
    public void Nothing_is_anchored_when_the_latest_point_is_not_a_file()
    {
        var (trail, sut) = Create();
        trail.RecordBrowser("https://example.com/", "Example");

        Assert.Null(sut.Anchor);
        Assert.False(sut.IsAwayFromAnchor(new(A, 500, 0)));
        Assert.Null(sut.TakeDeparture(arriving: null));
    }

    private (TrailViewModel Trail, TrailCaretCommitController Sut) Create()
    {
        var trail = new TrailViewModel(_store);
        trail.EnsureLoaded();
        var sut = new TrailCaretCommitController(trail, () => false,
            () => null, path => _carets.TryGetValue(path, out var at) ? at : null, _ => { });
        return (trail, sut);
    }
}
