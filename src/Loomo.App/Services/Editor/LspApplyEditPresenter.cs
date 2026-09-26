using System.Windows.Threading;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>LSP server initiated workspace/applyEdit をUIスレッドで適用し、応答結果をサーバーへ返す。</summary>
internal sealed class LspApplyEditPresenter
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<LspWorkspaceEdit, WorkspaceEditOutcome> _apply;
    private readonly Action<string> _showStatus;

    public LspApplyEditPresenter(
        Dispatcher dispatcher,
        Func<LspWorkspaceEdit, WorkspaceEditOutcome> apply,
        Action<string> showStatus)
    {
        _dispatcher = dispatcher;
        _apply = apply;
        _showStatus = showStatus;
    }

    public void Handle(object? sender, LspApplyEditEventArgs request)
    {
        // LSPの読取スレッドは応答を待っているため、UI dispatch を同期完了してから結果を返す。
        var outcome = _dispatcher.Invoke(() => _apply(request.Edit));
        request.Applied = outcome.Error is null;
        request.FailureReason = outcome.Error;

        if (outcome.Error is { } error)
            _dispatcher.BeginInvoke(new Action(() => _showStatus($"編集を適用できませんでした: {error}")));
    }
}
