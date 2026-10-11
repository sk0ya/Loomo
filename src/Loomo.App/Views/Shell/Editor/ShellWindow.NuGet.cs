using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.CSharp.Packages;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: NuGet パッケージ管理（設計書 §36）。入口はソリューションパネルの右クリックと
/// コマンドパレット。窓は 1 枚だけ持ち、もう一度開こうとしたら前面へ出す。
/// </summary>
public partial class ShellWindow
{
    private readonly NuGetPackageService _nuGetPackages = new();
    private NuGetPackagesWindow? _nuGetWindow;

    /// <param name="projectPath">チェックを付けて開くプロジェクト。null ならアクティブな C# ファイルの
    /// プロジェクト、それも無ければ全プロジェクト。</param>
    private void OpenNuGetPackageManager(string? projectPath = null)
    {
        if (_nuGetWindow is { IsLoaded: true } open)
        {
            open.Activate();
            return;
        }

        var solution = _solutionModel?.Current;
        if (solution is null || solution.Projects.Count == 0)
        {
            ShowRefactorStatus("NuGet：このワークスペースに C# プロジェクト（.csproj）が見つかりません。");
            return;
        }

        projectPath ??= ActiveCSharpEditor()?.FilePath is { } file ? solution.ProjectForFile(file)?.FullPath : null;
        var viewModel = new NuGetPackagesViewModel(_nuGetPackages, _solutionModel, solution, projectPath);
        _nuGetWindow = new NuGetPackagesWindow(viewModel) { Owner = this };
        _nuGetWindow.Closed += (_, _) => _nuGetWindow = null;
        _nuGetWindow.Show();
    }
}
