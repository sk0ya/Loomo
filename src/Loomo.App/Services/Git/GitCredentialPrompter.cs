using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// git・ssh の資格情報の問いを人に見せる（§24.101）。<see cref="sk0ya.Loomo.Services.GitAskPassServer"/> がパイプのスレッドから呼ぶので、
/// UI スレッドへ寄せて <see cref="GitCredentialDialog"/> で聞く。問いは一度に一つ——fetch --all が遠隔ごとに
/// 続けて聞いてきても、入力欄が重ならないよう順に出す。
/// </summary>
public sealed class GitCredentialPrompter : IGitCredentialPrompter
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<GitCredentialAnswer?> AskAsync(GitAskPass.Request request, GitCredentialPromptKind kind,
        GitCredentialRemember? remember, CancellationToken cancellationToken)
    {
        // FIDO 鍵の「触れてください」は答えを要らない。通知欄に出して、すぐ返す（ssh は触れ終わったら askpass を止める）。
        if (kind == GitCredentialPromptKind.Notice)
        {
            UiDispatch.Post(() => NotificationService.Info((request.Prompt ?? "").Trim(), "Git"));
            return new GitCredentialAnswer("");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return null;

        try
        {
            await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            return await dispatcher.InvokeAsync(
                () => GitCredentialDialog.Show(OwnerWindow(), request, kind, remember, cancellationToken));
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>覚えさせた結果は通知欄へ（入力欄はもう閉じているので）。</summary>
    public void Report(string message, bool success)
        => UiDispatch.Post(() =>
        {
            if (success) NotificationService.Success(message, "Git");
            else NotificationService.Warning(message, "Git");
        });

    /// <summary>いま手前にある Loomo の窓（切り離したペインの窓で操作していれば、その上に出す）。</summary>
    private static Window? OwnerWindow()
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w is not GitCredentialDialog)
               ?? app.MainWindow;
    }
}
