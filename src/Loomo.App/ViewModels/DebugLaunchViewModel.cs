using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグの起動・停止・再起動・ステップ実行・手動ビルド・例外オプションを扱うサブ ViewModel。
/// ヘッダのデバッグツールバーとエディタの実行系操作（カーソル行まで実行・次のステートメント設定・特定関数へステップイン）の窓口。
/// 起動構成（対象・引数・環境変数等）とプロファイルは全セッション共有の「入り口」なので 1 個のままだが、
/// 「開始」は必ず<b>新しいセッション</b>を作る（既存セッションは止めない）。続行/ステップ/中断/停止/再起動は
/// <see cref="DebugViewModel.ActiveSession"/>（今デバッグペインに表示中のセッション）に対して行う。</summary>
public sealed partial class DebugLaunchViewModel : ObservableObject
{
    private readonly DebugViewModel _manager;
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly DebugAttachViewModel _attach;
    private readonly DebugProfilesViewModel _profiles;

    /// <summary>デバッグ対象（<c>*.dll</c>/<c>*.exe</c>）の明示指定。空ならワークスペースから自動検出する。</summary>
    [ObservableProperty] private string _targetProgram = "";

    /// <summary>起動前に <c>dotnet build</c> を実行するか。</summary>
    [ObservableProperty] private bool _buildFirst = true;

    /// <summary>プログラムへ渡すコマンドライン引数（空白区切り・二重引用符でグループ化）。空なら引数なし。</summary>
    [ObservableProperty] private string _launchArgs = "";

    /// <summary>起動時に追加する環境変数（1 行 1 件 <c>KEY=VALUE</c>）。空なら親プロセスの環境のまま。</summary>
    [ObservableProperty] private string _launchEnv = "";

    /// <summary>マイコードのみをデバッグするか（VS の「マイ コードのみ」）。次回起動から反映。</summary>
    [ObservableProperty] private bool _justMyCode;

    /// <summary>例外ブレーク：スローされたすべての例外で中断（netcoredbg フィルタ <c>all</c>）。</summary>
    [ObservableProperty] private bool _breakOnAllExceptions;

    /// <summary>例外ブレーク：未処理（ユーザーコード外へ抜ける）例外で中断（フィルタ <c>user-unhandled</c>）。</summary>
    [ObservableProperty] private bool _breakOnUncaughtExceptions;

    /// <summary>netcoredbg の導入コマンド（促しバーのボタン用）。</summary>
    public string AdapterInstallCommand => DebugAdapterCatalog.Netcoredbg.InstallCommand ?? "";

    internal DebugLaunchViewModel(DebugViewModel manager, IWorkspaceService workspace, ITerminalService terminal,
        DebugAttachViewModel attach, DebugProfilesViewModel profiles)
    {
        _manager = manager;
        _workspace = workspace;
        _terminal = terminal;
        _attach = attach;
        _profiles = profiles;
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
        if (session.Kind == DebugSessionKind.Attach && session.AttachedProcess is { } proc)
            await _attach.RelaunchIntoAsync(session, proc);
        else
            await RelaunchIntoAsync(session);
    }

    private bool CanStart() => !_manager.IsTaskRunning;

    /// <summary>デバッグを開始する。既存セッションは止めず、常に<b>新しいセッション</b>を作って始める。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _manager.RequestOutput();  // 押下時に即「出力」へ
        _manager.Refresh();
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
            _workspace, _terminal, _manager, TargetProgram, BuildFirst, _profiles.SelectedProjectPath);
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
            _workspace, _terminal, _manager, TargetProgram, BuildFirst, _profiles.SelectedProjectPath);
        if (program is null) return;
        await LaunchIntoAsync(session, program);
    }

    private async Task LaunchIntoAsync(DebugSessionViewModel session, string program)
    {
        var iSession = (IDebugSession)session;
        var token = iSession.BeginSession();
        try
        {
            await session.DebugService.StartAsync(
                new DebugLaunchConfig(program, Path.GetDirectoryName(program),
                    Args: DebugLaunchArgs.ParseArgs(LaunchArgs),
                    Environment: DebugLaunchArgs.ParseEnv(LaunchEnv),
                    JustMyCode: JustMyCode),
                token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            iSession.Append(DebugOutputCategory.Important, $"デバッグ起動でエラー: {ex.Message}");
        }
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
            var result = await _terminal.RunCommandAsync(
                $"dotnet build \"{target}\" -c Debug --nologo", CancellationToken.None);
            _manager.WriteConsole(result.Output);
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
