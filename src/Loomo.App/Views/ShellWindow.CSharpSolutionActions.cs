using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.CSharp.Build;
using sk0ya.Loomo.CSharp.Debug;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.CSharp.Testing;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    /// <summary>Solution ExplorerのBuild／Test。対象は選択ノードのsln／csproj、出力は可視ターミナルへ流し、
    /// 同じ全文をProblemsのBuild診断へ渡す。デバッグ起動とは別タスクとして扱う。</summary>
    private async void OnCSharpSolutionActionRequested(
        object? sender,
        CSharpSolutionActionEventArgs e)
    {
        if (e.Node.FullPath is not { Length: > 0 } target)
            return;
        if (_terminal.IsExecuting || _vm.Debug.IsTaskRunning)
        {
            ShowRefactorStatus("別のBuild／Testが実行中です。");
            return;
        }

        target = Path.GetFullPath(target);

        if (e.Action is CSharpSolutionAction.FixAllProject or CSharpSolutionAction.FixAllSolution)
        {
            var model = _solutionModel?.Current;
            var project = e.Action == CSharpSolutionAction.FixAllProject
                ? model?.Projects.FirstOrDefault(candidate =>
                    string.Equals(Path.GetFullPath(candidate.FullPath), target,
                        StringComparison.OrdinalIgnoreCase))
                : model?.Projects.FirstOrDefault(candidate => candidate.State == ProjectLoadState.Ready);
            if (project is null)
            {
                ShowRefactorStatus("Fix all対象のC#プロジェクトが見つかりません。");
                return;
            }

            await RunCSharpFixAllAsync(project.FullPath,
                e.Action == CSharpSolutionAction.FixAllProject
                    ? CSharpFixAllScope.Project : CSharpFixAllScope.Solution);
            return;
        }

        if (!File.Exists(target))
        {
            ShowRefactorStatus($"対象が見つかりません: {target}");
            return;
        }

        if (e.Action == CSharpSolutionAction.DebugTests)
        {
            await _vm.Debug.Tests.DebugProjectTestsAsync(
                target, e.Node.Kind == CSharpSolutionNodeKind.Solution);
            return;
        }

        if (e.Action == CSharpSolutionAction.Run)
        {
            // Solution ExplorerのRunもDebugペインと同じ起動プロジェクト文脈を使う。
            // 既に選択中のプロジェクトなら、ユーザーが選んだlaunchSettingsプロファイルを保持できる。
            var selected = _vm.Debug.Profiles.AvailableProjects.FirstOrDefault(project =>
                string.Equals(Path.GetFullPath(project.FullPath), target,
                    StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                _vm.Debug.Profiles.SelectedProject = selected;
        }

        if (e.Action == CSharpSolutionAction.Debug)
        {
            var project = new DebugProjectDiscovery.ProjectEntry(
                Path.GetFileNameWithoutExtension(target), target, Path.GetFileName(target),
                e.Node.CanRunTests);
            if (_vm.Debug.Launch.RunProjectCommand.CanExecute(project))
                await _vm.Debug.Launch.RunProjectCommand.ExecuteAsync(project);
            else
                ShowRefactorStatus("デバッグを開始できません。");
            return;
        }

        var tfm = e.Node.Kind == CSharpSolutionNodeKind.Project
            ? _solutionModel?.Current.Projects.FirstOrDefault(project =>
                string.Equals(Path.GetFullPath(project.FullPath), target,
                    StringComparison.OrdinalIgnoreCase))?.SelectedTargetFramework
            : null;
        var configuration = _solutionModel?.Current.ConfigurationForTarget(target) ?? "Debug";
        var launchProfile = e.Action == CSharpSolutionAction.Run
            ? _vm.Debug.Profiles.SelectedRunLaunchProfileFor(target)
            : null;
        var actionName = e.Action switch
        {
            CSharpSolutionAction.Test => "テスト",
            CSharpSolutionAction.Run => "実行",
            _ => "ビルド",
        };
        // IDE ペインのヘッダーと Solution Explorer の見出しは同じ文言を出す。片方だけ
        // 更新し忘れると「テスト中…」のまま止まって見えるので、必ず両方をここから書く。
        void SetStatus(string message)
        {
            _vm.Debug.StatusMessage = message;
            if (_vm.CSharpSolutionExplorer is { } target)
                target.ExecutionStatusText = message;
        }

        _vm.Debug.RequestOutput();
        _vm.Debug.IsTaskRunning = true;
        SetStatus(e.Action == CSharpSolutionAction.Test
            ? "テスト中…" : e.Action == CSharpSolutionAction.Run ? "実行中…" : "ビルド中…");
        CSharpTestExecutionResult? testExecution = null;
        try
        {
            CommandResult result;
            if (launchProfile?.IsIisExpress == true)
            {
                var command = IisExpressLaunchCommand.Build(target, launchProfile, out var launchError);
                if (command is null)
                {
                    SetStatus($"{actionName}失敗");
                    ShowRefactorStatus($"IIS Expressを起動できません: {launchError}");
                    return;
                }
                result = await _terminal.RunCommandInVisibleTerminalAsync(command, CancellationToken.None);
            }
            else if (e.Action == CSharpSolutionAction.Build)
                result = await CSharpBuildService.RunAsync(
                    _terminal, target, configuration, CancellationToken.None, tfm);
            else if (e.Action == CSharpSolutionAction.Test)
            {
                testExecution = await CSharpTestExecutionService.RunAsync(
                    _terminal, target, null, configuration, CancellationToken.None,
                    targetFramework: tfm);
                if (testExecution.PreparationError is { } preparationError)
                {
                    SetStatus($"{actionName}失敗");
                    ShowRefactorStatus(preparationError);
                    return;
                }
                if (testExecution.Command is not { } testResult)
                {
                    SetStatus($"{actionName}失敗");
                    ShowRefactorStatus("テストを実行できませんでした。");
                    return;
                }
                // Solution Explorerの実行結果もTest Explorerの一覧・集計・ガターへ戻す。
                _vm.Debug.Tests.ApplyExternalExecutionResult(testExecution);
                result = testResult;
            }
            else
            {
                var launchProfileName = launchProfile is { IsSupported: true, Name.Length: > 0 }
                    ? launchProfile.Name
                    : null;
                result = await CSharpRunService.RunAsync(
                    _terminal, target, configuration, tfm, launchProfileName, CancellationToken.None);
            }

            _vm.Debug.WriteConsole(result.Output);
            _vm.Debug.ReportBuildOutput(result.Output, Path.GetDirectoryName(target));
            SetStatus(result.Success
                ? $"{actionName}成功"
                : $"{actionName}失敗（{result.ExitCode}）");
            ShowRefactorStatus(result.Success
                ? $"{actionName}が完了しました: {Path.GetFileName(target)}"
                : $"{actionName}に失敗しました: {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            SetStatus($"{actionName}失敗");
            ShowRefactorStatus($"{actionName}を実行できませんでした: {ex.Message}");
        }
        finally
        {
            if (testExecution is not null)
                CSharpTestExecutionService.CleanupResults(testExecution);
            _vm.Debug.IsTaskRunning = false;
        }
    }

}
