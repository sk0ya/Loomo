using System.Diagnostics;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Services;

/// <summary>C#コマンドの非同期失敗をコマンド名付きで通知する。</summary>
internal static class CSharpCommandOperationController
{
    internal static async Task RunAsync(
        string commandId, Func<Task> operation, Action<string> showFailure)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            var title = CSharpEditorCommandCatalog.All
                .FirstOrDefault(command => string.Equals(command.Id, commandId, StringComparison.Ordinal))
                ?.Title ?? commandId;
            Debug.WriteLine($"[C#] {title} に失敗: {ex}");
            showFailure($"「{title}」を実行できませんでした: {ex.Message}");
        }
    }
}
