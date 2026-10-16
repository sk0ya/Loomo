namespace sk0ya.Loomo.App.Services;

/// <summary>非モーダルなトースト通知の発行口。単純な結果通知（成功・失敗）はここを通す。
/// ユーザーの決定（Yes/No・OK/Cancel）が要る確認は引き続き MessageBox.Show を使う
/// （トーストは自動で消えるため、応答を待つ必要がある操作には使えない）。
/// 実体は <see cref="NotificationService"/> への薄い入口で、出したものは軌跡バー右端の通知欄の履歴にも残る。
/// 出所や「その場でできること」を添えたいとき、トーストを出さず履歴にだけ載せたいときは
/// <see cref="NotificationService.Post"/> を直接使う。</summary>
public static class ToastService
{
    public static void Info(string message) => NotificationService.Post(message, NotificationKind.Info);
    public static void Success(string message) => NotificationService.Post(message, NotificationKind.Success);
    public static void Warning(string message) => NotificationService.Post(message, NotificationKind.Warning);
    public static void Error(string message) => NotificationService.Post(message, NotificationKind.Error);
}
