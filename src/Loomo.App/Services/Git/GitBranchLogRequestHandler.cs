using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブランチログ切り替えの要求順を管理し、古い要求からのエラー表示を抑える。</summary>
internal sealed class GitBranchLogRequestHandler
{
    private int _request;

    internal async Task ShowAsync(
        GitSessionViewModel viewModel,
        GitBranchInfo branch,
        Action beforeRequest)
    {
        beforeRequest();
        var request = ++_request;
        try
        {
            await viewModel.ShowBranchLogAsync(branch);
        }
        catch (Exception exception)
        {
            if (request == _request)
            {
                viewModel.StatusIsError = true;
                viewModel.StatusMessage = exception.Message;
            }
        }
    }
}
