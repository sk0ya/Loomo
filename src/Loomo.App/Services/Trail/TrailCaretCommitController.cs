using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>同じファイルの中で遠くへ移ったキャレットを、軌跡の点にする（§27.3.3）。
/// <para>File の点はタブの活性化でしか積まれないので、同じファイルの中の定義ジャンプ・検索・<c>gg</c>/<c>G</c>・
/// アウトラインからの移動は一つも残らなかった。経路は数十あるので経路ごとには書かず、エディタの戻る／進む
/// （<see cref="EditorLocationHistory"/>）と同じくキャレットの動きだけから判定する。</para>
/// <list type="bullet">
/// <item>軌跡が立っている場所（<see cref="Anchor"/>）から <see cref="EditorLocationHistory.JumpThresholdLines"/> 行以上離れ、
/// そこに <see cref="DwellTime"/> とどまったら 1 点（<c>j</c> の押しっぱなしやページ送りの途中は点にしない）。</item>
/// <item>とどまらずに別の地点へ移ったときは <see cref="TakeDeparture"/> が離れた場所を返す——そこが「離れた場所」なので、
/// 近くの点の行を遠くへ書き換えるのではなく別の点にする。</item>
/// </list></summary>
internal sealed class TrailCaretCommitController(
    TrailViewModel trail,
    Func<bool> isSuppressed,
    Func<TrailFilePosition?> activePosition,
    Func<string, TrailFilePosition?> positionOfFile,
    Action<TrailFilePosition> commit)
{
    public static readonly TimeSpan DwellTime = TimeSpan.FromSeconds(2);

    private TrailFilePosition? _landed;
    private DispatcherTimer? _timer;

    /// <summary>軌跡がいま立っているとみなす場所。軌跡から過去の点へ戻ったならその着地点、
    /// そうでなければ最新の点（ファイルか編集のときだけ）。</summary>
    public TrailFilePosition? Anchor => _landed ?? trail.LatestFilePosition;

    /// <summary>アクティブなエディタのキャレットが動いた。</summary>
    public void OnCaretMoved()
    {
        if (isSuppressed() || activePosition() is not { } here || !IsAwayFromAnchor(here))
        {
            _timer?.Stop();
            return;
        }
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    /// <summary><paramref name="here"/> が立っている場所と同じファイルの、遠い行か。別ファイルはタブの活性化が記録する。</summary>
    public bool IsAwayFromAnchor(TrailFilePosition here)
        => Anchor is { } anchor
           && string.Equals(anchor.Path, here.Path, StringComparison.OrdinalIgnoreCase)
           && !TrailLogic.IsNearLine(anchor.Line, here.Line);

    /// <summary>別の地点を積む直前：立っている場所から遠くにいたなら、その位置を返す（呼び出し側が点にする）。
    /// <paramref name="arriving"/>（これから積む地点の位置）の近くなら返さない——編集の点が同じ場所を代表する。</summary>
    public TrailFilePosition? TakeDeparture(TrailFilePosition? arriving)
    {
        _timer?.Stop();
        if (Anchor is not { } anchor || positionOfFile(anchor.Path) is not { } here || !IsAwayFromAnchor(here))
            return null;
        if (arriving is { } next && string.Equals(next.Path, here.Path, StringComparison.OrdinalIgnoreCase)
            && TrailLogic.IsNearLine(next.Line, here.Line))
            return null;
        return here;
    }

    /// <summary>軌跡から戻って <paramref name="at"/> に着いた（最新の点ではない場所に立っている）。</summary>
    public void Land(TrailFilePosition? at)
    {
        _timer?.Stop();
        _landed = at;
    }

    /// <summary>新しい点が積まれた：以後は最新の点が立っている場所。</summary>
    public void Recorded() => _landed = null;

    public void Reset()
    {
        _timer?.Stop();
        _landed = null;
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = DwellTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!isSuppressed() && activePosition() is { } here && IsAwayFromAnchor(here))
                commit(here);
        };
        return timer;
    }
}
