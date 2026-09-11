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

    /// <summary>帯に並べる順（＝ペインの並び順）。<c>PaneKind</c> は全種がドック可能なので、
    /// 集中モードの <c>StageOrder</c> と違ってトレースも含めた全種をここに並べる。</summary>
    public static readonly PaneKind[] DockOrder =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport, PaneKind.Git,
        PaneKind.Diff, PaneKind.Ai, PaneKind.Debug, PaneKind.TsIde, PaneKind.Search, PaneKind.Files,
        PaneKind.Trace,
    ];

    /// <summary>既定の割り当て。書く／読む／見るための面は中央に残し、
    /// 道具（履歴・シェル・ビルド・検索・一覧）は下、本文の脇に添える面は右へ置く。</summary>
    public static IReadOnlyDictionary<PaneKind, DockRegion> DefaultRegions { get; } =
        new Dictionary<PaneKind, DockRegion>
        {
            [PaneKind.Editor] = DockRegion.Center,
            [PaneKind.Browser] = DockRegion.Center,
            [PaneKind.Diff] = DockRegion.Center,
            [PaneKind.Ai] = DockRegion.Center,
            [PaneKind.EditorSupport] = DockRegion.Right,
            [PaneKind.Terminal] = DockRegion.Bottom,
            [PaneKind.Git] = DockRegion.Bottom,
            [PaneKind.Debug] = DockRegion.Bottom,
            [PaneKind.TsIde] = DockRegion.Bottom,
            [PaneKind.Search] = DockRegion.Bottom,
            [PaneKind.Files] = DockRegion.Bottom,
            [PaneKind.Trace] = DockRegion.Bottom,
        };

    private readonly Dictionary<PaneKind, DockRegion> _regions = new(DefaultRegions);

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

    public bool Enter()
    {
        if (Active)
            return false;
        Active = true;
        return true;
    }

    public bool Exit()
    {
        if (!Active)
            return false;
        Active = false;
        return true;
    }

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
        if (previous == region)
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
        if (CenterPane is { } current && RegionOf(current) == DockRegion.Center && applicable(current))
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
        _regions.Clear();
        foreach (var (kind, region) in DefaultRegions)
            _regions[kind] = region;
        if (regions is not null)
            foreach (var (kind, region) in regions)
                _regions[kind] = region;
        CenterPane = centerPane is { } center && RegionOf(center) == DockRegion.Center ? center : null;
        // 立っている面があるなら閉じてはいない。閉じた印だけが残ると、次の起動で中央が埋まらない。
        CenterClosed = centerClosed && CenterPane is null;
        BottomPane = bottomPane is { } bottom && RegionOf(bottom) == DockRegion.Bottom ? bottom : null;
        RightPane = rightPane is { } right && RegionOf(right) == DockRegion.Right ? right : null;
        SetBottomHeight(bottomHeight ?? DefaultBottomHeight);
        SetRightWidth(rightWidth ?? DefaultRightWidth);
    }

    /// <summary>既定と違う割り当てだけを保存する（既定を変えたときに保存済みの部屋が置いていかれないように）。</summary>
    public IEnumerable<KeyValuePair<PaneKind, DockRegion>> ChangedRegions()
        => _regions.Where(pair =>
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
