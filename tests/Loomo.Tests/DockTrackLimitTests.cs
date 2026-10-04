using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.Tests;

/// <summary>右／下の領域の上限。広い窓で決めた幅のまま窓を縮めると、中央が潰れたあと右帯ごと
/// 窓の外へ押し出されていた（検索の「詳細」や右帯が見えない）分の回帰テスト。</summary>
public class DockTrackLimitTests
{
    /// <summary>器から固定の枠と中央の最小幅を引いた残りが上限。</summary>
    [Fact]
    public void Leaves_room_for_the_center_and_fixed_tracks()
        => Assert.Equal(1187 - (300 + 6 + 6 + 48) - 160, DockTrackLimit.Max(1187, 300 + 6 + 6 + 48, 160));

    /// <summary>どれだけ狭くても、掴めないほど細くはしない。</summary>
    [Fact]
    public void Never_goes_below_the_floor()
        => Assert.Equal(DockTrackLimit.Floor, DockTrackLimit.Max(400, 360, 160));

    /// <summary>まだ測っていない（起動直後の 0）うちは抑えない——保存幅が一瞬で潰れないように。</summary>
    [Fact]
    public void Unmeasured_container_imposes_no_limit()
        => Assert.Equal(double.PositiveInfinity, DockTrackLimit.Max(0, 360, 160));
}
