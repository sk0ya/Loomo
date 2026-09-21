namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ状態の変更を、デバッグビューの表示タブ選択へ写す。</summary>
internal static class DebugSessionTabPolicy
{
    internal static void ApplyForStateChange(
        bool stoppedChanged,
        bool isStopped,
        bool busyChanged,
        bool isBusy,
        Action showInspection,
        Action showOutput)
    {
        if (stoppedChanged)
        {
            if (isStopped)
                showInspection();
            else
                showOutput();
        }
        else if (busyChanged && !isBusy)
            showOutput();
    }
}
