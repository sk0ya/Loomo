namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>危険操作（コマンド実行・書込）のユーザー承認。</summary>
public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct);
}
