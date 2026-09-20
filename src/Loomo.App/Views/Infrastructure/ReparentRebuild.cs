using System;
using System.Windows;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// <b>別ウィンドウへ載せ替えられた</b>ことを検出して、中身の作り直しを呼ぶ。
///
/// <para>WebView2（コンポジション版）はコンポジションビジュアルが生成時の窓のコンポジタに紐づいたままで、
/// 再ペアレントしても新しい窓へは移らず<b>空表示</b>になる。切り離しウィンドウのタブは窓をまたいで
/// 移動できる（<see cref="Detach.DetachedWindowManager"/> が実体を再ペアレントする）ので、
/// WebView2 を抱える器はどれもこの合図で作り直す必要がある。</para>
///
/// <para>合図に <c>Unloaded</c>→<c>Loaded</c> を使えるのは確認済み——載せ替えは
/// 「外す・足す」が同じディスパッチャパスで済むので保留中の2つが相殺されないか疑わしいが、
/// <b>移動先が別ウィンドウ（＝別の PresentationSource）なら両方とも発火する</b>（実測）。
/// 相殺されるのは同じ窓の中で付け替えたときで、その場合はコンポジタも変わらないので作り直しは要らない。
/// 逆に窓を閉じるときは <c>Unloaded</c> だけが来て <c>Loaded</c> は来ないため、何も起きない。</para>
/// </summary>
internal static class ReparentRebuild
{
    /// <summary><paramref name="host"/> が別ウィンドウへ移されたら <paramref name="rebuild"/> を呼ぶ。
    /// 初めて窓へ載せるとき（<c>Unloaded</c> を伴わない <c>Loaded</c>）は呼ばない——生成直後の実体は
    /// そのまま使えるので、作り直すと表示が無駄に1往復する。</summary>
    public static void Watch(FrameworkElement host, Action rebuild)
    {
        var reattachPending = false;
        host.Unloaded += (_, _) => reattachPending = true;
        host.Loaded += (_, _) =>
        {
            if (!reattachPending)
                return;
            reattachPending = false;
            rebuild();
        };
    }
}
