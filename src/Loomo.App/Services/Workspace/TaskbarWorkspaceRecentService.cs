using System.IO;
using System.Windows;
using System.Windows.Shell;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペースを Windows タスクバーの「最近使った項目」に登録する。</summary>
public sealed class TaskbarWorkspaceRecentService
{
    private readonly JumpList? _jumpList;

    public TaskbarWorkspaceRecentService()
    {
        try
        {
            if (Application.Current is null)
                return;

            var jumpList = new JumpList { ShowRecentCategory = true };
            JumpList.SetJumpList(Application.Current, jumpList);
            _jumpList = jumpList;
        }
        catch
        {
            // タスクバー連携が使えない環境でも、ワークスペース本体は起動できるようにする。
        }
    }

    public void AddRecent(WorkspaceSnapshot workspace)
    {
        var folder = workspace.RootPath;
        var applicationPath = Environment.ProcessPath;
        if (_jumpList is null || string.IsNullOrWhiteSpace(folder)
            || !Directory.Exists(folder) || string.IsNullOrWhiteSpace(applicationPath))
            return;

        try
        {
            folder = Path.GetFullPath(folder);
            var title = string.IsNullOrWhiteSpace(workspace.CustomName)
                ? WorkspaceListViewModel.DisplayName(folder)
                : workspace.CustomName!;

            JumpList.AddToRecentCategory(new JumpTask
            {
                ApplicationPath = applicationPath,
                Arguments = StartupArguments.FormatWorkspaceArgument(folder),
                Description = $"Loomo で {folder} を開く",
                IconResourcePath = applicationPath,
                IconResourceIndex = 0,
                Title = title,
                WorkingDirectory = folder
            });
        }
        catch
        {
            // Jump List は OS／シェル側の状態に左右されるため、失敗しても本体の操作を妨げない。
        }
    }
}
