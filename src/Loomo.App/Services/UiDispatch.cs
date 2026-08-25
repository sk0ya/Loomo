using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>UI スレッドへ寄せてから実行する。寄せ先が無い（ヘッドレス）・もう回っていない
/// （終了処理中）・すでに UI スレッドの上、のいずれでもその場で実行する。
/// git のポーリングスレッドやコマンド完了スレッドから読み直しを起こすときの入口。
///
/// <para><see cref="Application.Current"/> が null かどうかだけで「ヘッドレスか」を決めると、
/// <b>Application は在るのにそのディスパッチャがもう回っていない</b>ときに
/// <c>BeginInvoke</c> が永久に実行されず、読み直しが黙って消える——アプリ終了処理中と、
/// テストで STA ホスト（<c>WpfViewHost</c>）を畳んだ後がこれで、後者は Git ペイン／Diff ペインの
/// テストが「基準を切り替えても一覧が更新されない」形で落ちていた。</para></summary>
public static class UiDispatch
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished
            || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.BeginInvoke(action);
    }
}
