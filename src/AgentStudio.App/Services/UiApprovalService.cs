using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentStudio.Core.Abstractions;

namespace AgentStudio.App.Services;

/// <summary>
/// 承認要求を UI（AIバー）へ橋渡しする。AiBarViewModel が ApprovalRequested を購読し、
/// インラインの承認カードを表示して TaskCompletionSource を完了させる。
/// </summary>
public sealed class UiApprovalService : IApprovalService
{
    public event Action<ApprovalContext>? ApprovalRequested;

    public Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new ApprovalContext(toolName, summary, tcs);
        ct.Register(() => tcs.TrySetResult(false));

        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
            ApprovalRequested?.Invoke(ctx);
        else
            app.Dispatcher.Invoke(() => ApprovalRequested?.Invoke(ctx));

        return tcs.Task;
    }
}

public sealed record ApprovalContext(string ToolName, string Summary, TaskCompletionSource<bool> Completion);
