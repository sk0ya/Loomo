using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.CSharp.Build;
using sk0ya.Loomo.CSharp.Debug;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグの起動・停止・再起動・ステップ実行・手動ビルド・例外オプションを扱うサブ ViewModel。
/// ヘッダのデバッグツールバーとエディタの実行系操作（カーソル行まで実行・次のステートメント設定・特定関数へステップイン）の窓口。
/// 起動構成（対象・引数・環境変数等）とプロファイルは全セッション共有の「入り口」なので 1 個のままだが、
/// 「開始」は必ず<b>新しいセッション</b>を作る（既存セッションは止めない）。続行/ステップ/中断/停止/再起動は
/// <see cref="DebugViewModel.ActiveSession"/>（今デバッグペインに表示中のセッション）に対して行う。</summary>
public sealed partial class DebugLaunchViewModel : ObservableObject, ILaunchConfigurationOwner, ILaunchBrowserTarget
{
    private readonly DebugViewModel _manager;
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly DebugAttachViewModel _attach;
    private readonly DebugProfilesViewModel _profiles;
    private readonly ISolutionModelService? _solutionModel;
    private readonly IBrowserService? _browser;
    private Process? _iisExpressProcess;
    private string? _iisExpressProjectPath;
    private LaunchSettingsProfile? _iisExpressProfile;

    /// <summary>デバッグ対象（<c>*.dll</c>/<c>*.exe</c>）の明示指定。空ならワークスペースから自動検出する。</summary>
    [ObservableProperty] private string _targetProgram = "";

    /// <summary>起動前に <c>dotnet build</c> を実行するか。</summary>
    [ObservableProperty] private bool _buildFirst = true;

    /// <summary>プログラムへ渡すコマンドライン引数（空白区切り・二重引用符でグループ化）。空なら引数なし。</summary>
    [ObservableProperty] private string _launchArgs = "";

    /// <summary>起動時に追加する環境変数（1 行 1 件 <c>KEY=VALUE</c>）。空なら親プロセスの環境のまま。</summary>
    [ObservableProperty] private string _launchEnv = "";

    /// <summary>起動時の作業ディレクトリ。空なら実行対象のディレクトリを使う。</summary>
    [ObservableProperty] private string _launchWorkingDirectory = "";

    /// <summary>launchSettings.jsonのlaunchBrowser/applicationUrl/launchUrlから得た実効URL。空なら自動遷移しない。</summary>
    [ObservableProperty] private string _launchBrowserUrl = "";

    /// <summary>マイコードのみをデバッグするか（VS の「マイ コードのみ」）。次回起動から反映。</summary>
    [ObservableProperty] private bool _justMyCode;

    /// <summary>例外ブレーク：スローされたすべての例外で中断（netcoredbg フィルタ <c>all</c>）。</summary>
    [ObservableProperty] private bool _breakOnAllExceptions;

    /// <summary>例外ブレーク：未処理（ユーザーコード外へ抜ける）例外で中断（フィルタ <c>user-unhandled</c>）。</summary>
    [ObservableProperty] private bool _breakOnUncaughtExceptions;

    /// <summary>netcoredbg の導入コマンド（促しバーのボタン用）。</summary>
    public string AdapterInstallCommand => DebugAdapterCatalog.Netcoredbg.InstallCommand ?? "";

    internal DebugLaunchViewModel(DebugViewModel manager, IWorkspaceService workspace, ITerminalService terminal,
        DebugAttachViewModel attach, DebugProfilesViewModel profiles,
        ISolutionModelService? solutionModel = null, IBrowserService? browser = null)
    {
        _manager = manager;
        _workspace = workspace;
        _terminal = terminal;
        _attach = attach;
        _profiles = profiles;
        _solutionModel = solutionModel;
        _browser = browser;
        _manager.SessionStateChanged += OnSessionStateChanged;
    }

    private void OnSessionStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepIntoCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
        BuildTargetCommand.NotifyCanExecuteChanged();
        RunProjectCommand.NotifyCanExecuteChanged();
        RunSelectedProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnBreakOnAllExceptionsChanged(bool value) => _ = ApplyExceptionFiltersAsync();
    partial void OnBreakOnUncaughtExceptionsChanged(bool value) => _ = ApplyExceptionFiltersAsync();

    private IReadOnlyList<string> CurrentExceptionFilterIds()
    {
        var ids = new List<string>();
        if (BreakOnAllExceptions) ids.Add("all");
        if (BreakOnUncaughtExceptions) ids.Add("user-unhandled");
        return ids;
    }

    /// <summary>例外ブレークのフィルタ選択を、実行中/起動中の全セッションのアダプタへ反映する
    /// （未起動でも記憶され、起動時に送られる）。</summary>
    private Task ApplyExceptionFiltersAsync()
    {
        var ids = CurrentExceptionFilterIds();
        return Task.WhenAll(_manager.Sessions.Select(s => s.DebugService.SetExceptionBreakpointsAsync(ids, CancellationToken.None)));
    }

    private bool CanStep() => _manager.IsStopped;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task Continue() => ActiveDebugServiceOrNull()?.ContinueAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOver() => ActiveDebugServiceOrNull()?.StepOverAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepInto() => ActiveDebugServiceOrNull()?.StepInAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOut() => ActiveDebugServiceOrNull()?.StepOutAsync() ?? Task.CompletedTask;

    /// <summary>実行中（停止していない）ときだけ一時停止できる。</summary>
    private bool CanPause() => _manager.IsBusy && !_manager.IsStopped;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task Pause() => ActiveDebugServiceOrNull()?.PauseAsync() ?? Task.CompletedTask;

    private bool CanRestart() => _manager.IsBusy;

    /// <summary>アクティブなセッションを停止して、同じ対象で再起動する（直前が launch なら再 launch、attach なら再 attach）。
    /// 新しいセッションは作らず、同じセッション（同じタブ）を使い回す。</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task Restart()
    {
        var session = _manager.ActiveSession;
        if (session is null) return;
        await session.DebugService.StopAsync();
        if (session.Kind == DebugSessionKind.IisExpress)
            await RelaunchIisExpressIntoAsync(session);
        else if (session.Kind == DebugSessionKind.Attach && session.AttachedProcess is { } proc)
            await _attach.RelaunchIntoAsync(session, proc);
        else
            await RelaunchIntoAsync(session);
    }

    private bool CanStart() => !_manager.IsTaskRunning;

    /// <summary>デバッグを開始する。既存セッションは止めず、常に<b>新しいセッション</b>を作って始める。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => StartCoreAsync(null);

    /// <summary>「実行」タブのプロジェクト行から起動する。構成に保存された実行対象を一時的に上書きし、
    /// 行で選んだプロジェクトの出力 DLL を起動する。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RunProject(DebugProjectDiscovery.ProjectEntry? project)
    {
        if (project is null) return Task.CompletedTask;
        _profiles.SelectedProject = project;
        return StartCoreAsync(ReferenceEquals(project, DebugProjectDiscovery.AutoDetect)
            ? null
            : project.FullPath);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RunSelectedProject() => RunProject(_profiles.SelectedProject);

    private async Task StartCoreAsync(string? projectOverride)
    {
        _manager.RequestOutput();  // 押下時に即「出力」へ
        _manager.Refresh();
        if (_profiles.SelectedLaunchSettingsProfile is { IsIisExpress: true } iisProfile)
        {
            await StartIisExpressDebugAsync(projectOverride, iisProfile);
            return;
        }
        if (_manager.IsAdapterMissing)
        {
            _manager.StatusMessage = "アダプタ未導入";
            _manager.Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        // 直前セッションが対象プログラムの自然終了で終わっていた場合、アダプタの後始末（dll/pdb のハンドル解放）
        // が非同期に進んでいる可能性がある。先にビルドすると「ファイル使用中」で失敗し得るため、ここで待つ。
        await _manager.WaitForAllIdleAsync();

        var program = await DebugTargetResolver.ResolveProgramAsync(
            _workspace, _terminal, _manager, TargetProgram, BuildFirst,
            projectOverride ?? _profiles.SelectedProjectPath,
            ConfigurationFor(projectOverride ?? _profiles.SelectedProjectPath),
            SelectedTargetFrameworkFor(projectOverride ?? _profiles.SelectedProjectPath));
        if (program is null) return;

        var session = _manager.CreateSession(BuildDisplayName(program), DebugSessionKind.Launch);
        await session.DebugService.SetExceptionBreakpointsAsync(CurrentExceptionFilterIds(), CancellationToken.None);
        await LaunchIntoAsync(session, program);
    }

    /// <summary>同じ対象で、既存セッション（同じタブ）へ再度 launch する（Restart 用）。</summary>
    private async Task RelaunchIntoAsync(DebugSessionViewModel session)
    {
        _manager.RequestOutput();
        await _manager.WaitForAllIdleAsync();
        var program = await DebugTargetResolver.ResolveProgramAsync(
            _workspace, _terminal, _manager, TargetProgram, BuildFirst, _profiles.SelectedProjectPath,
            ConfigurationFor(_profiles.SelectedProjectPath), SelectedTargetFrameworkFor(_profiles.SelectedProjectPath));
        if (program is null) return;
        await LaunchIntoAsync(session, program);
    }

    /// <summary>IIS Expressを先に起動し、そのプロセスへnetcoredbgをattachする。
    /// IIS Expressはdotnetのlaunch対象DLLではないため、通常のlaunch経路へ無理に混ぜず、
    /// 既存のAttach実装を使ってデバッグセッションを作る。</summary>
    private async Task StartIisExpressDebugAsync(string? projectOverride, LaunchSettingsProfile profile)
    {
        if (_manager.IsAdapterMissing)
        {
            _manager.StatusMessage = "アダプタ未導入";
            _manager.Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        var projectPath = projectOverride ?? _profiles.SelectedProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            _manager.StatusMessage = "IIS Expressのプロジェクト未選択";
            _manager.Append(DebugOutputCategory.Important,
                "IIS Expressをデバッグするには起動プロジェクトを選択してください。");
            return;
        }

        await _manager.WaitForAllIdleAsync();
        await StopIisExpressProcessAsync();

        if (BuildFirst && !await BuildIisExpressProjectAsync(projectPath))
            return;

        var spec = IisExpressLaunchCommand.CreateSpec(projectPath, profile, out var specError);
        if (spec is null)
        {
            _manager.StatusMessage = "IIS Express起動失敗";
            _manager.Append(DebugOutputCategory.Important, $"IIS Expressを起動できません: {specError}");
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec.ExecutablePath,
                WorkingDirectory = spec.ProjectDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        foreach (var argument in spec.ProcessArguments)
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var pair in spec.EnvironmentVariables)
            process.StartInfo.Environment[pair.Key] = pair.Value;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("iisexpress.exeのプロセスを開始できませんでした。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            _manager.StatusMessage = "IIS Express起動失敗";
            _manager.Append(DebugOutputCategory.Important, $"IIS Expressを起動できません: {ex.Message}");
            return;
        }

        _iisExpressProcess = process;
        _iisExpressProjectPath = Path.GetFullPath(projectPath);
        _iisExpressProfile = profile;
        process.Exited += OnIisExpressExited;

        var processModel = new DebugProcessViewModel(process.Id, "iisexpress", null, true);
        var session = _manager.CreateSession($"IIS Express: {profile.Name}", DebugSessionKind.IisExpress);
        session.AttachedProcess = processModel;
        await session.DebugService.SetExceptionBreakpointsAsync(CurrentExceptionFilterIds(), CancellationToken.None);
        await _attach.AttachIntoExistingAsync(session, processModel);

        if (session.DebugService.State is DebugSessionState.Failed)
        {
            _manager.Append(DebugOutputCategory.Important,
                "IIS ExpressプロセスへのDAP attachに失敗したため、起動したプロセスを停止します。");
            await StopIisExpressProcessAsync();
            return;
        }

        if (_browser is not null && profile.BrowserUrl is { Length: > 0 } url
            && session.DebugService.State is DebugSessionState.Running or DebugSessionState.Stopped)
            _ = ShowLaunchBrowserAsync(url, (IDebugSession)session);
    }

    private async Task<bool> BuildIisExpressProjectAsync(string projectPath)
    {
        _manager.IsTaskRunning = true;
        _manager.StatusMessage = "IIS Expressのビルド中…";
        try
        {
            var result = await CSharpBuildService.RunAsync(
                _terminal, projectPath, ConfigurationFor(projectPath), CancellationToken.None,
                SelectedTargetFrameworkFor(projectPath));
            _manager.WriteConsole(result.Output);
            _manager.ReportBuildOutput(result.Output, Path.GetDirectoryName(projectPath));
            if (result.Success) return true;

            _manager.StatusMessage = $"IIS Expressのビルド失敗（{result.ExitCode}）";
            _manager.Append(DebugOutputCategory.Important,
                $"IIS Expressを起動せず、ビルド失敗（終了コード {result.ExitCode}）で停止しました。");
            return false;
        }
        catch (Exception ex)
        {
            _manager.StatusMessage = "IIS Expressのビルド失敗";
            _manager.Append(DebugOutputCategory.Important, $"IIS Express用ビルドを実行できません: {ex.Message}");
            return false;
        }
        finally { _manager.IsTaskRunning = false; }
    }

    private async Task RelaunchIisExpressIntoAsync(DebugSessionViewModel session)
    {
        var projectPath = _iisExpressProjectPath ?? _profiles.SelectedProjectPath;
        var profile = _iisExpressProfile ?? _profiles.SelectedLaunchSettingsProfile;
        if (string.IsNullOrWhiteSpace(projectPath) || profile is not { IsIisExpress: true })
        {
            session.StatusMessage = "IIS Expressの再起動情報がありません";
            return;
        }

        await StopIisExpressProcessAsync();
        if (BuildFirst && !await BuildIisExpressProjectAsync(projectPath))
            return;

        var spec = IisExpressLaunchCommand.CreateSpec(projectPath, profile, out var specError);
        if (spec is null)
        {
            session.StatusMessage = "IIS Express再起動失敗";
            ((IDebugSession)session).Append(DebugOutputCategory.Important,
                $"IIS Expressを再起動できません: {specError}");
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec.ExecutablePath,
                WorkingDirectory = spec.ProjectDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        foreach (var argument in spec.ProcessArguments)
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var pair in spec.EnvironmentVariables)
            process.StartInfo.Environment[pair.Key] = pair.Value;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("iisexpress.exeを開始できませんでした。");
            var processModel = new DebugProcessViewModel(process.Id, "iisexpress", null, true);
            _iisExpressProcess = process;
            _iisExpressProjectPath = Path.GetFullPath(projectPath);
            _iisExpressProfile = profile;
            process.Exited += OnIisExpressExited;
            session.AttachedProcess = processModel;
            await _attach.AttachIntoExistingAsync(session, processModel);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ((IDebugSession)session).Append(DebugOutputCategory.Important,
                $"IIS Express再起動に失敗しました: {ex.Message}");
            await StopIisExpressProcessAsync();
        }
    }

    private void OnIisExpressExited(object? sender, EventArgs e)
    {
        if (sender is not Process process || !ReferenceEquals(_iisExpressProcess, process)) return;
        _iisExpressProcess = null;
        process.Dispose();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(() =>
                _manager.Append(DebugOutputCategory.Important, "IIS Expressプロセスが終了しました。")));
        else
            _manager.Append(DebugOutputCategory.Important, "IIS Expressプロセスが終了しました。");
    }

    private async Task StopIisExpressProcessAsync()
    {
        var process = _iisExpressProcess;
        _iisExpressProcess = null;
        if (process is null) return;
        process.Exited -= OnIisExpressExited;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
        finally { process.Dispose(); }
    }

    internal Task DisposeIisExpressAsync() => StopIisExpressProcessAsync();

    private string? SelectedTargetFrameworkFor(string? projectPath)
        => string.IsNullOrWhiteSpace(projectPath)
            ? null
            : _solutionModel?.ProjectForTarget(projectPath)?.SelectedTargetFramework;

    /// <summary>アプリ終了時の同期後始末。UIスレッドのDisposeから非同期待機を行わず、
    /// 起動したIIS Expressだけを停止する。</summary>
    internal void DisposeIisExpress()
    {
        var process = _iisExpressProcess;
        _iisExpressProcess = null;
        if (process is null) return;
        process.Exited -= OnIisExpressExited;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    private async Task LaunchIntoAsync(DebugSessionViewModel session, string program)
    {
        var iSession = (IDebugSession)session;
        var token = iSession.BeginSession();
        try
        {
            await session.DebugService.StartAsync(
                new DebugLaunchConfig(program, ResolveWorkingDirectory(program),
                    Args: DebugLaunchArgs.ParseArgs(LaunchArgs),
                    Environment: DebugLaunchArgs.ParseEnv(LaunchEnv),
                    JustMyCode: JustMyCode),
                token);
            if (_browser is not null && !string.IsNullOrWhiteSpace(LaunchBrowserUrl)
                && session.DebugService.State is DebugSessionState.Running or DebugSessionState.Stopped)
                _ = ShowLaunchBrowserAsync(LaunchBrowserUrl, iSession);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            iSession.Append(DebugOutputCategory.Important, $"デバッグ起動でエラー: {ex.Message}");
        }
    }

    private async Task ShowLaunchBrowserAsync(string url, IDebugSession session)
    {
        try
        {
            await _browser!.ShowAndNavigateAsync(url, CancellationToken.None);
            session.Append(DebugOutputCategory.Important, $"launchSettingsのURLをブラウザペインに表示: {url}");
        }
        catch (Exception ex)
        {
            session.Append(DebugOutputCategory.Important, $"launchSettingsのブラウザ起動に失敗しました: {ex.Message}");
        }
    }

    private string? ResolveWorkingDirectory(string program)
    {
        var configured = LaunchWorkingDirectory.Trim();
        if (configured.Length == 0) return Path.GetDirectoryName(program);
        if (!Path.IsPathRooted(configured) && _workspace.PrimaryFolder is { } root)
            configured = Path.GetFullPath(Path.Combine(root, configured));
        return Directory.Exists(configured) ? configured : Path.GetDirectoryName(program);
    }

    private static string BuildDisplayName(string program) => Path.GetFileNameWithoutExtension(program);

    private bool CanStop() => _manager.IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        var session = _manager.ActiveSession;
        if (session is null) return;
        ((IDebugSession)session).CancelSession();
        await session.DebugService.StopAsync();
        if (session.Kind == DebugSessionKind.IisExpress)
            await StopIisExpressProcessAsync();
    }

    private bool CanRunTask() => !_manager.IsTaskRunning;

    /// <summary>ワークスペースをビルドする（デバッグ起動とは独立した手動ビルド）。
    /// 対象は .sln 優先、無ければ最初の .csproj。出力はコンソールへ、結果はステータスへ。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task BuildTarget()
    {
        var target = _manager.FindBuildTarget();
        if (target is null) return;

        _manager.RequestOutput();  // 押下時に即「出力」へ
        _manager.IsTaskRunning = true;
        try
        {
            // StartAsync 同様、直前セッションのアダプタ後始末が残っていれば「ファイル使用中」を避けるため待つ。
            await _manager.WaitForAllIdleAsync();
            _manager.StatusMessage = "ビルド中…";
            _manager.Append(DebugOutputCategory.Important, $"ビルド: {Path.GetFileName(target)}");
            var result = await CSharpBuildService.RunAsync(
                _terminal, target, ConfigurationFor(target), CancellationToken.None);
            _manager.WriteConsole(result.Output);
            _manager.ReportBuildOutput(result.Output);
            _manager.StatusMessage = result.Success ? "ビルド成功" : $"ビルド失敗（{result.ExitCode}）";
            _manager.Append(DebugOutputCategory.Important,
                result.Success ? "ビルドに成功しました。" : $"ビルドに失敗しました（終了コード {result.ExitCode}）。");
        }
        finally { _manager.IsTaskRunning = false; }
    }

    /// <summary>促しバーの「インストール」。導入コマンドを見えるターミナルで実行する。</summary>
    [RelayCommand]
    private void InstallAdapter()
    {
        if (!string.IsNullOrWhiteSpace(AdapterInstallCommand))
            _terminal.TryRunInVisibleTerminal(AdapterInstallCommand);
    }

    private IDebugService? ActiveDebugServiceOrNull() => _manager.ActiveSession?.DebugService;

    private string ConfigurationFor(string? targetPath)
        => _solutionModel?.Current.ConfigurationForTarget(targetPath) ?? "Debug";

    // --- エディタの実行系操作（右クリックメニュー。アクティブなセッションに対して行う） ---

    /// <summary>アダプタが「次のステートメントに設定」（gotoTargets/goto）に対応しているか。</summary>
    public bool SupportsSetNextStatement => ActiveDebugServiceOrNull()?.SupportsSetNextStatement ?? false;

    /// <summary>次に実行する文をエディタのカーソル行（0 始まり）へ移動する。成功したら実行行ハイライトと
    /// コールスタック/変数を更新する。</summary>
    public async Task SetNextStatementAsync(string sourcePath, int line0)
    {
        var session = _manager.ActiveSession;
        if (session is null || !session.IsStopped) return;
        var ok = await session.DebugService.SetNextStatementAsync(sourcePath, line0 + 1);  // エディタ0始まり → DAP1始まり
        if (ok)
        {
            session.NotifyExecutionLine(sourcePath, line0);
            await session.Inspection.LoadStackAsync();
        }
    }

    /// <summary>カーソル行（0 始まり）まで実行する（一時ブレークポイントを置いて続行）。停止中のみ有効。</summary>
    public Task RunToCursorAsync(string sourcePath, int line0)
    {
        var session = _manager.ActiveSession;
        return session is { IsStopped: true }
            ? session.DebugService.RunToCursorAsync(sourcePath, line0 + 1, CancellationToken.None)  // エディタ0始まり → DAP1始まり
            : Task.CompletedTask;
    }

    /// <summary>アダプタが「特定の関数にステップ イン」（stepInTargets）に対応しているか。</summary>
    public bool SupportsStepInTargets => ActiveDebugServiceOrNull()?.SupportsStepInTargets ?? false;

    /// <summary>停止行のステップ イン候補（先頭フレーム文脈）を取得する。停止していなければ空。</summary>
    public Task<IReadOnlyList<DebugStepInTarget>> GetStepInTargetsAsync()
    {
        var session = _manager.ActiveSession;
        return session is { IsStopped: true } && session.Inspection.SelectedFrame is { } f
            ? session.DebugService.GetStepInTargetsAsync(f.Id)
            : Task.FromResult((IReadOnlyList<DebugStepInTarget>)Array.Empty<DebugStepInTarget>());
    }

    /// <summary>指定の候補へステップ インする。</summary>
    public Task StepIntoTargetAsync(DebugStepInTarget target) => ActiveDebugServiceOrNull()?.StepInTargetAsync(target.Id) ?? Task.CompletedTask;
}
