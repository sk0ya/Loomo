using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Layout;

/// <summary>ドックモードでペインが住む領域。</summary>
public enum DockRegion
{
    /// <summary>中央＝主役の面を1枚立てる場所（畳めない）。</summary>
    Center,
    /// <summary>下のツールウィンドウ領域。</summary>
    Bottom,
    /// <summary>右のツールウィンドウ領域。</summary>
    Right
}

/// <summary>ドックモード（IDE 風ツールウィンドウ）のUI非依存な状態を所有する。
/// <para><b>3つの領域（中央・下・右）すべてが「同時に1枚」</b>で、帯のアイコンがその1枚を選ぶ
/// （＝アイコンがタブを兼ねる）。中央は主役の面を立てる場所、下／右は道具を添える場所だが、
/// <b>畳めるかどうかに差は付けない</b>——中央だけ閉じられないと、下や右に面が出ていても
/// 中央の面は閉じられず、閉じたつもりの人には別の面へ切り替わったようにしか見えない。
/// <see cref="StageModeCoordinator"/> と同じく、この状態はレイアウトツリーに一切触らない
/// ——分割モードへ戻せば元のタイル配置がそのまま戻る。</para></summary>
public sealed class DockLayoutCoordinator
{
    public const double DefaultBottomHeight = 260;
    public const double DefaultRightWidth = 320;
    public const double MinBottomHeight = 80;
    public const double MaxBottomHeight = 1200;
    public const double MinRightWidth = 160;
    public const double MaxRightWidth = 1200;

    /// <summary>帯に並べる順（＝ペインの並び順）。<b>ここに無い面はドックに出ない。</b>
    /// 顔ぶれは集中モードの <c>StageOrder</c> と揃える——トレース（<see cref="PaneKind.Trace"/>）は
    /// 部屋の面としては出しておらず、ビュー・スイッチャーにもコマンドパレットにも並ばないので、
    /// ドックの帯だけがその面を出す唯一の入口になっていた。
    /// <para>Diff も置かない——ドックでは差分は別ウィンドウで開く（<c>ShellWindow.ShowDiff</c>）。
    /// 中央の1枚を差分で差し替えると、書いていたエディタが見えなくなる。</para></summary>
    public static readonly PaneKind[] DockOrder =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport, PaneKind.Git,
        PaneKind.Ai, PaneKind.Debug, PaneKind.TsIde, PaneKind.Search, PaneKind.Files,
    ];

    /// <summary>ドックに出せる面か（＝<see cref="DockOrder"/> に居るか）。
    /// <para>出せない面の <see cref="RegionOf"/> は既定の <see cref="DockRegion.Center"/> を返すので、
    /// 保存された割り当てをそのまま信じると、帯に取っ手の無い面が中央に立ってしまう。
    /// だから保存・復元はここを通す。</para></summary>
    public static bool IsDockable(PaneKind kind) => Array.IndexOf(DockOrder, kind) >= 0;

    /// <summary>既定の割り当て。<b>自分で打つ面</b>——書く（エディタ）・打つ（ターミナル）・読む（ブラウザ）・
    /// 尋ねる（AI）——は中央に置き、手を動かさずに<b>眺める道具</b>（履歴・ビルド・検索・一覧）は下、
    /// 本文の脇に添える面は右へ置く。
    /// <para><b>ターミナルは下の道具ではなく中央の面。</b>下の領域は主役を押しのけない高さ
    /// （<see cref="DefaultBottomHeight"/>）で、覗きに行く場所としては足りても<b>居座って打つ場所</b>
    /// には足りない。打っている間はそれが主役なので、中央で本文と同じ大きさを取る——並びは
    /// <see cref="DockOrder"/> のとおりエディタの次、AI より前。</para></summary>
    public static IReadOnlyDictionary<PaneKind, DockRegion> DefaultRegions { get; } =
        new Dictionary<PaneKind, DockRegion>
        {
            [PaneKind.Editor] = DockRegion.Center,
            [PaneKind.Terminal] = DockRegion.Center,
            [PaneKind.Browser] = DockRegion.Center,
            [PaneKind.Ai] = DockRegion.Center,
            [PaneKind.EditorSupport] = DockRegion.Right,
            [PaneKind.Git] = DockRegion.Bottom,
            [PaneKind.Debug] = DockRegion.Bottom,
            [PaneKind.TsIde] = DockRegion.Bottom,
            [PaneKind.Search] = DockRegion.Bottom,
            [PaneKind.Files] = DockRegion.Bottom,
        };

    /// <summary>ドックをまだ一度も組んでいない部屋を復元するときに、下／右へ出しておく面。
    /// <para>ドックは新しい部屋の既定モード（<c>WorkspaceSessionCoordinator.DefaultDisplayMode</c>）なので、
    /// 初回の見え方＝部屋の第一印象になる。中央だけ立って下も右も畳んであると、帯のアイコンを
    /// 総当たりするまで「ここに何が住めるのか」が判らない——道具が一つずつ出ている姿を先に見せる。
    /// <b>一度でもドックを組んだ部屋（<see cref="Configured"/>）には効かない</b>——そこでの null は
    /// 「畳んである」という意思表示で、既定で埋め直すと畳む操作が無かったことになる。</para>
    /// <para>下はGit——ターミナルが中央へ移った（<see cref="DefaultRegions"/>）ので、下に出せる面のうち
    /// <b>どの部屋にも必ずある</b>のはここだけ（デバッグ・IDE は部屋を選び、検索・一覧は開いた人が
    /// 探しに行く面）。初めて入る部屋で「いま何が変わっているか」が最初に読めるのも道理に合う。</para></summary>
    public const PaneKind InitialBottomPane = PaneKind.Git;
    public const PaneKind InitialRightPane = PaneKind.EditorSupport;

    private readonly Dictionary<PaneKind, DockRegion> _regions = new(DefaultRegions);
    private bool _configured;

    public bool Active { get; private set; }

    /// <summary>中央に立っているペイン。閉じてあるか、置ける面が1つも無ければ null。</summary>
    public PaneKind? CenterPane { get; private set; }

    /// <summary>中央を閉じてあるか。
    /// <para>閉じた中央を <see cref="EnsureCenterPane"/> が埋め直さないための印。これが無いと
    /// 次の組み立てで別の面が繰り上がり、閉じたはずが切り替わったようにしか見えない。
    /// 「置ける面が無くて空」とは別物なので、案内の文面もここで分ける。</para></summary>
    public bool CenterClosed { get; private set; }

    /// <summary>下の領域に出ているペイン。null＝畳んである。</summary>
    public PaneKind? BottomPane { get; private set; }

    /// <summary>右の領域に出ているペイン。null＝畳んである。</summary>
    public PaneKind? RightPane { get; private set; }

    public double BottomHeight { get; private set; } = DefaultBottomHeight;
    public double RightWidth { get; private set; } = DefaultRightWidth;

    /// <summary>この部屋でドックを組んだことがあるか（＝下／右の畳んである状態が、既定ではなく
    /// その人の意思として読める印）。<see cref="InitialBottomPane"/> の既定を当てるかどうかの判定に使う。</summary>
    public bool Configured => _configured;

    public bool Enter()
    {
        if (Active)
            return false;
        Active = true;
        _configured = true;   // 一度でも入れば、以降の畳んである状態は既定ではなくその人の選択
        return true;
    }

    public bool Exit()
    {
        if (!Active)
            return false;
        Active = false;
        return true;
    }

    /// <summary>ドック表示を保存形式へ投影する。</summary>
    public DockSnapshot CaptureSnapshot() => new()
    {
        Placements = ChangedRegions()
            .Select(pair => new DockPlacementSnapshot { Kind = pair.Key, Region = pair.Value })
            .ToList(),
        CenterPane = CenterPane,
        CenterClosed = CenterClosed,
        BottomPane = BottomPane,
        RightPane = RightPane,
        BottomHeight = BottomHeight,
        RightWidth = RightWidth,
        Configured = _configured,
    };

    /// <summary>保存形式からドック状態を復元する。<b>まだドックを組んでいない部屋は既定の見え方で開く</b>
    /// （<see cref="InitialBottomPane"/>／<see cref="InitialRightPane"/>）。
    /// <para>その判定は <see cref="WasArranged"/>（組んだ痕跡があるか）であって「保存が無いこと」ではない
    /// ——<c>ShellWindow.CaptureInto</c> はモードを問わず毎回 <see cref="CaptureSnapshot"/> を書くので、
    /// ドックへ一度も入っていない部屋の保存にも「下も右も null（＝畳んである）」の
    /// <see cref="DockSnapshot"/> が入っている。保存の有無で判ると、既存の部屋が初めてドックを
    /// 押したときこそ<b>中央だけの空っぽ</b>で迎えることになる。</para></summary>
    public void Restore(bool active, DockSnapshot? snapshot)
    {
        var configured = WasArranged(snapshot);
        Restore(active,
            snapshot?.Placements?.Select(placement =>
                new KeyValuePair<PaneKind, DockRegion>(placement.Kind, placement.Region)),
            snapshot?.CenterPane,
            snapshot?.CenterClosed ?? false,
            configured
                ? Rehome(snapshot!.BottomPane, DockRegion.Bottom, snapshot.Placements, InitialBottomPane)
                : InitialBottomPane,
            configured
                ? Rehome(snapshot!.RightPane, DockRegion.Right, snapshot.Placements, InitialRightPane)
                : InitialRightPane,
            snapshot?.BottomHeight,
            snapshot?.RightWidth);
        _configured |= configured;
    }

    /// <summary><b>既定の引っ越しで、その領域に出せなくなった保存値</b>を読み替える。
    /// <para>保存に明示の割り当てが無い面は、保存された時点の<see cref="DefaultRegions"/>で
    /// そこに居ただけ（<see cref="ChangedRegions"/> は既定と同じ割り当てを書かない）。だから既定が
    /// 引っ越すと、その保存値は<b>今はもうその領域から出せない面</b>になる——ターミナルが下から
    /// 中央へ移ったときがこれで、下に端末を出して暮らしていた部屋は、更新した初回に
    /// <b>下が空のまま畳まれ、端末はどこにも立たない</b>姿で開いていた。落として畳むのではなく、
    /// 組んでいない部屋と同じ既定（<paramref name="initial"/>）でその領域を埋める。</para>
    /// <para>明示の割り当てがある面は<b>その人が動かした</b>もの。保存どうしの食い違い
    /// （右へ動かした面が下に立っている等）は読み替えずに落とす。畳んであった（null）のも意思なので
    /// 埋めない。</para></summary>
    private static PaneKind? Rehome(
        PaneKind? saved, DockRegion region,
        List<DockPlacementSnapshot>? placements, PaneKind? initial)
    {
        if (saved is not { } pane)
            return null;
        var placed = placements?.FirstOrDefault(placement => placement.Kind == pane);
        var effective = placed?.Region
            ?? (DefaultRegions.TryGetValue(pane, out var fallback) ? fallback : DockRegion.Center);
        if (effective == region)
            return pane;
        return placed is null ? initial : null;
    }

    /// <summary>この保存はドックを組んだ部屋のものか（＝初回の既定を当ててはいけないか）。
    /// <para><b>印がある保存は印を信じる</b>（<see cref="DockSnapshot.Configured"/> が
    /// <c>true</c>／<c>false</c> のどちらでも）。信じないと<b>初回の見え方が一度きりになる</b>
    /// ——初回の既定はモードを問わず毎回書かれる保存に乗るので（<c>CaptureInto</c> は
    /// ドック中でなくても <see cref="CaptureSnapshot"/> を書く）、次の起動では「下に面がある」
    /// 保存として返ってくる。中身だけで見分けると、<b>自分が書いた既定を人の意思と読み違えて</b>
    /// 組んだ部屋に化けてしまい、以後この既定はその部屋へ二度と届かない。実際それで、
    /// ターミナルが中央へ移った後に初めてドックを押した部屋は、下に出しようのない面
    /// （＝落とされて空）で迎えることになっていた。</para>
    /// <para>印の無い保存＝この項目より前に書かれたものだけを<b>中身で見分ける</b>——立てた面・
    /// 畳んだ先・動かした割り当てが1つでも残っていれば、その人はドックを組んでいる。
    /// 中身も見ないと、ターミナルを<em>わざわざ</em>畳んでドックで暮らしていた部屋が、
    /// 更新したとたん既定で埋め直される。</para></summary>
    private static bool WasArranged(DockSnapshot? snapshot)
        => snapshot is not null
           && (snapshot.Configured
               ?? (snapshot.CenterClosed
                   || snapshot.CenterPane is not null
                   || snapshot.BottomPane is not null
                   || snapshot.RightPane is not null
                   || snapshot.Placements is { Count: > 0 }));

    public DockRegion RegionOf(PaneKind kind)
        => _regions.TryGetValue(kind, out var region) ? region : DockRegion.Center;

    /// <summary>中央に置かれたペインか（ドックモードでないときは全部がタイルの住人）。</summary>
    public bool IsInTile(PaneKind kind) => !Active || RegionOf(kind) == DockRegion.Center;

    /// <summary>ドック帯にアイコンが出るペインか（＝下か右）。</summary>
    public bool IsDocked(PaneKind kind) => RegionOf(kind) != DockRegion.Center;

    /// <summary><paramref name="region"/> に割り当てられたペインを並び順で。</summary>
    public IEnumerable<PaneKind> PanesIn(DockRegion region)
        => DockOrder.Where(kind => RegionOf(kind) == region);

    public PaneKind? OpenPaneIn(DockRegion region) => region switch
    {
        DockRegion.Bottom => BottomPane,
        DockRegion.Right => RightPane,
        _ => CenterPane,
    };

    /// <summary>操作対象を画面に出ているペインへ寄せる。</summary>
    public PaneKind? ResolveShownPane(PaneKind? preferred)
        => preferred is { } kind && IsOpen(kind)
            ? kind
            : OpenPanes().Cast<PaneKind?>().FirstOrDefault();

    /// <summary>現在のモードに並べるペインを選ぶ。</summary>
    public static IEnumerable<PaneKind> PaneOrderForMode(
        IEnumerable<PaneKind> stageOrder, bool dockActive)
        => dockActive ? stageOrder.Where(IsDockable) : stageOrder;

    /// <summary>親から外さず各ドック領域に据えておくペインを返す。</summary>
    public IReadOnlyCollection<PaneKind> PanesToKeepAttached(
        bool fullscreen, Func<IEnumerable<PaneKind>> wingKinds)
        => fullscreen ? Array.Empty<PaneKind>()
            : Active ? OpenPanes().ToList()
            : wingKinds().ToArray();

    /// <summary>軌跡へ保存する現在のドック配置キー。</summary>
    public string? LayoutKey()
        => Active
            ? $"{CenterPane?.ToString() ?? "-"}+{BottomPane?.ToString() ?? "-"}+{RightPane?.ToString() ?? "-"}"
            : null;

    /// <summary>領域内を指定方向へ巡回する。対象が1つ以下なら切替先は無い。</summary>
    public PaneKind? NextInRegion(DockRegion region, PaneKind? current, int direction, Func<PaneKind, bool> applicable)
    {
        var panes = PanesIn(region).Where(applicable).ToList();
        if (panes.Count == 0)
            return null;
        if (current is null)
            return panes[0];
        if (panes.Count <= 1)
            return null;
        var index = panes.IndexOf(current.Value);
        if (index < 0)
            return panes[0];
        return panes[((index + direction) % panes.Count + panes.Count) % panes.Count];
    }

    /// <summary>いまその領域に出て見えているペインか。</summary>
    public bool IsOpen(PaneKind kind) => Active && OpenPaneIn(RegionOf(kind)) == kind;

    /// <summary>いま出ている面を<b>中央→下→右</b>の順で（畳んだ領域は飛ばす）。
    /// 「親から外してはいけない面」と「畳んだ面からフォーカスを渡す先」は同じ並びで要るので、
    /// 順序はここ1か所で決める。</summary>
    public IEnumerable<PaneKind> OpenPanes()
    {
        if (!Active)
            yield break;
        if (CenterPane is { } center)
            yield return center;
        if (BottomPane is { } bottom)
            yield return bottom;
        if (RightPane is { } right)
            yield return right;
    }

    /// <summary>ドック帯のアイコンを押したときの動作。出ていれば畳み、そうでなければ出す。
    /// 3領域とも同じ。状態が変わったら true。</summary>
    public bool Toggle(PaneKind kind)
        => OpenPaneIn(RegionOf(kind)) == kind ? Close(RegionOf(kind)) : Open(kind);

    /// <summary>そのペインをその領域の1枚にする（畳んでいれば開き、別の面が出ていれば差し替える）。
    /// 状態が変わったら true。</summary>
    public bool Open(PaneKind kind)
    {
        // 帯に取っ手の無い面は出さない（FocusPane 等から来ても、中央に居座らせない）。
        if (!IsDockable(kind))
            return false;
        var region = RegionOf(kind);
        if (OpenPaneIn(region) == kind)
            return false;
        SetOpenPane(region, kind);
        if (region == DockRegion.Center)
            CenterClosed = false;
        return true;
    }

    /// <summary>その領域を畳む。<b>中央も畳める</b>——閉じると決めた人に、残りの領域の様子で
    /// 断る理由は無い。空いた中央には案内が出て、帯のアイコンから戻せる。</summary>
    public bool Close(DockRegion region)
    {
        if (OpenPaneIn(region) is null)
            return false;
        SetOpenPane(region, null);
        if (region == DockRegion.Center)
            CenterClosed = true;
        return true;
    }

    /// <summary>ペインの住む領域を変える。移した先では必ず出し直す
    /// （移動先で畳んだままだと、掴んだ面がどこへ消えたのか判らない）。
    /// 元が中央だと中央は空のまま返るので、呼び手は <see cref="EnsureCenterPane"/> で選び直す。</summary>
    public bool Place(PaneKind kind, DockRegion region)
    {
        var previous = RegionOf(kind);
        if (!IsDockable(kind) || previous == region)
            return false;
        if (OpenPaneIn(previous) == kind)
            SetOpenPane(previous, null);
        _regions[kind] = region;
        SetOpenPane(region, kind);
        if (region == DockRegion.Center)
            CenterClosed = false;
        return true;
    }

    /// <summary>中央が空／その部屋に無い面なら、中央に割り当てられた面から選び直す
    /// （置ける面が1つも無ければ空のまま＝案内を出す）。
    /// <b>閉じてあるときは埋めない</b>——ここで繰り上げると、閉じる操作が切り替える操作になる。</summary>
    public void EnsureCenterPane(Func<PaneKind, bool> applicable)
    {
        if (CenterClosed)
            return;
        if (CenterPane is { } current && IsDockable(current) && RegionOf(current) == DockRegion.Center
            && applicable(current))
            return;
        CenterPane = PanesIn(DockRegion.Center)
            .Where(applicable)
            .Cast<PaneKind?>()
            .FirstOrDefault();
    }

    public void SetBottomHeight(double height)
        => BottomHeight = Math.Clamp(height, MinBottomHeight, MaxBottomHeight);

    public void SetRightWidth(double width)
        => RightWidth = Math.Clamp(width, MinRightWidth, MaxRightWidth);

    /// <summary>ワークスペース復元。保存が無い項目は既定へ落とす。</summary>
    public void Restore(
        bool active,
        IEnumerable<KeyValuePair<PaneKind, DockRegion>>? regions,
        PaneKind? centerPane,
        bool centerClosed,
        PaneKind? bottomPane,
        PaneKind? rightPane,
        double? bottomHeight,
        double? rightWidth)
    {
        Active = active;
        // ドックで開く部屋は、その時点で組んである部屋（次の保存から畳んだ状態が意思として残る）。
        _configured = active;
        _regions.Clear();
        foreach (var (kind, region) in DefaultRegions)
            _regions[kind] = region;
        if (regions is not null)
            foreach (var (kind, region) in regions)
                if (IsDockable(kind))
                    _regions[kind] = region;
        CenterPane = centerPane is { } center && IsDockable(center) && RegionOf(center) == DockRegion.Center
            ? center
            : null;
        // 立っている面があるなら閉じてはいない。閉じた印だけが残ると、次の起動で中央が埋まらない。
        CenterClosed = centerClosed && CenterPane is null;
        BottomPane = bottomPane is { } bottom && RegionOf(bottom) == DockRegion.Bottom ? bottom : null;
        RightPane = rightPane is { } right && RegionOf(right) == DockRegion.Right ? right : null;
        SetBottomHeight(bottomHeight ?? DefaultBottomHeight);
        SetRightWidth(rightWidth ?? DefaultRightWidth);
    }

    /// <summary>既定と違う割り当てだけを保存する（既定を変えたときに保存済みの部屋が置いていかれないように）。</summary>
    public IEnumerable<KeyValuePair<PaneKind, DockRegion>> ChangedRegions()
        => _regions.Where(pair => IsDockable(pair.Key))
            .Where(pair =>
                !DefaultRegions.TryGetValue(pair.Key, out var fallback) || fallback != pair.Value);

    /// <summary>その領域から出せる面が1つも無くなったら畳む（IDE の無い部屋で IDE を開いたまま
    /// 復元する、のような宙ぶらりんを作らない）。</summary>
    public void DropInapplicable(Func<PaneKind, bool> applicable)
    {
        if (CenterPane is { } center && !applicable(center))
            CenterPane = null;
        if (BottomPane is { } bottom && !applicable(bottom))
            BottomPane = null;
        if (RightPane is { } right && !applicable(right))
            RightPane = null;
        EnsureCenterPane(applicable);
    }

    private void SetOpenPane(DockRegion region, PaneKind? kind)
    {
        switch (region)
        {
            case DockRegion.Bottom:
                BottomPane = kind;
                break;
            case DockRegion.Right:
                RightPane = kind;
                break;
            default:
                CenterPane = kind;
                break;
        }
    }
}
