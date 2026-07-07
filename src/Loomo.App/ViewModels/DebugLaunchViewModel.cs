using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグの起動・停止・再起動・ステップ実行・手動ビルド・例外オプションを扱うサブ ViewModel。
/// ヘッダのデバッグツールバーとエディタの実行系操作（カーソル行まで実行・次のステートメント設定・特定関数へステップイン）の窓口。</summary>
public sealed partial class DebugLaunchViewModel : ObservableObject
{
    private readonly IDebugService _debug;
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly IDebugSession _session;
    private readonly DebugInspectionViewModel _inspection;
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

    internal DebugLaunchViewModel(IDebugService debug, IWorkspaceService workspace, ITerminalService terminal,
        IDebugSession session, DebugInspectionViewModel inspection, DebugAttachViewModel attach,
        DebugProfilesViewModel profiles)
    {
        _debug = debug;
        _workspace = workspace;
        _terminal = terminal;
        _session = session;
        _inspection = inspection;
        _attach = attach;
        _profiles = profiles;
        _session.SessionStateChanged += OnSessionStateChanged;
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

    /// <summary>例外ブレークのフィルタ選択をアダプタへ反映する（未起動でも記憶され、起動時に送られる）。</summary>
    private Task ApplyExceptionFiltersAsync()
    {
        var ids = new List<string>();
        if (BreakOnAllExceptions) ids.Add("all");
        if (BreakOnUncaughtExceptions) ids.Add("user-unhandled");
        return _debug.SetExceptionBreakpointsAsync(ids, CancellationToken.None);
    }

    private bool CanStep() => _session.IsStopped;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task Continue() => await _debug.ContinueAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOver() => await _debug.StepOverAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepInto() => await _debug.StepInAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOut() => await _debug.StepOutAsync();

    /// <summary>実行中（停止していない）ときだけ一時停止できる。</summary>
    private bool CanPause() => _session.IsBusy && !_session.IsStopped;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task Pause() => await _debug.PauseAsync();

    private bool CanRestart() => _session.IsBusy;

    /// <summary>セッションを停止して同じ対象で再起動する（直前が launch なら再 launch、attach なら再 attach）。</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task Restart()
    {
        var attach = _attach.LastAttachProcess;
        await StopAsync();
        if (attach is not null) await _attach.AttachToAsync(attach);
        else await StartAsync();
    }

    private bool CanStart() => !_session.IsBusy && !_session.IsTaskRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _session.RequestOutput();  // 押下時に即「出力」へ（IsBusy は起動後まで立たない）
        _session.RefreshAdapter();
        if (_session.IsAdapterMissing)
        {
            _session.StatusMessage = "アダプタ未導入";
            _session.Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        // 直前セッションが対象プログラムの自然終了で終わっていた場合、アダプタの後始末（dll/pdb のハンドル解放）
        // が非同期に進んでいる可能性がある。先にビルドすると「ファイル使用中」で失敗し得るため、ここで待つ。
        await _debug.WaitForIdleAsync();

        var program = await DebugTargetResolver.ResolveProgramAsync(
            _workspace, _terminal, _session, TargetProgram, BuildFirst, _profiles.SelectedProjectPath);
        if (program is null) return;

        _attach.ClearLastAttach();  // この起動は launch（再起動で再 launch する）
        var token = _session.BeginSession();
        try
        {
            await _debug.StartAsync(
                new DebugLaunchConfig(program, Path.GetDirectoryName(program),
                    Args: DebugLaunchArgs.ParseArgs(LaunchArgs),
                    Environment: DebugLaunchArgs.ParseEnv(LaunchEnv),
                    JustMyCode: JustMyCode),
                token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            _session.Append(DebugOutputCategory.Important, $"デバッグ起動でエラー: {ex.Message}");
        }
    }

    private bool CanStop() => _session.IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        _session.CancelSession();
        await _debug.StopAsync();
    }

    private bool CanRunTask() => !_session.IsBusy && !_session.IsTaskRunning;

    /// <summary>ワークスペースをビルドする（デバッグ起動とは独立した手動ビルド）。
    /// 対象は .sln 優先、無ければ最初の .csproj。出力はコンソールへ、結果はステータスへ。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task BuildTarget()
    {
        var target = _session.FindBuildTarget();
        if (target is null) return;

        _session.RequestOutput();  // 押下時に即「出力」へ
        _session.IsTaskRunning = true;
        try
        {
            // StartAsync 同様、直前セッションのアダプタ後始末が残っていれば「ファイル使用中」を避けるため待つ。
            await _debug.WaitForIdleAsync();
            _session.StatusMessage = "ビルド中…";
            _session.Append(DebugOutputCategory.Important, $"ビルド: {Path.GetFileName(target)}");
            var result = await _terminal.RunCommandAsync(
                $"dotnet build \"{target}\" -c Debug --nologo", CancellationToken.None);
            _session.WriteConsole(result.Output);
            _session.StatusMessage = result.Success ? "ビルド成功" : $"ビルド失敗（{result.ExitCode}）";
            _session.Append(DebugOutputCategory.Important,
                result.Success ? "ビルドに成功しました。" : $"ビルドに失敗しました（終了コード {result.ExitCode}）。");
        }
        finally { _session.IsTaskRunning = false; }
    }

    /// <summary>促しバーの「インストール」。導入コマンドを見えるターミナルで実行する。</summary>
    [RelayCommand]
    private void InstallAdapter()
    {
        if (!string.IsNullOrWhiteSpace(AdapterInstallCommand))
            _terminal.TryRunInVisibleTerminal(AdapterInstallCommand);
    }

    // --- エディタの実行系操作（右クリックメニュー） ---

    /// <summary>アダプタが「次のステートメントに設定」（gotoTargets/goto）に対応しているか。</summary>
    public bool SupportsSetNextStatement => _debug.SupportsSetNextStatement;

    /// <summary>次に実行する文をエディタのカーソル行（0 始まり）へ移動する。成功したら実行行ハイライトと
    /// コールスタック/変数を更新する。</summary>
    public async Task SetNextStatementAsync(string sourcePath, int line0)
    {
        if (!_session.IsStopped) return;
        var ok = await _debug.SetNextStatementAsync(sourcePath, line0 + 1);  // エディタ0始まり → DAP1始まり
        if (ok)
        {
            _session.RaiseExecutionLine(sourcePath, line0);
            await _inspection.LoadStackAsync();
        }
    }

    /// <summary>カーソル行（0 始まり）まで実行する（一時ブレークポイントを置いて続行）。停止中のみ有効。</summary>
    public Task RunToCursorAsync(string sourcePath, int line0)
        => _session.IsStopped
            ? _debug.RunToCursorAsync(sourcePath, line0 + 1, CancellationToken.None)  // エディタ0始まり → DAP1始まり
            : Task.CompletedTask;

    /// <summary>アダプタが「特定の関数にステップ イン」（stepInTargets）に対応しているか。</summary>
    public bool SupportsStepInTargets => _debug.SupportsStepInTargets;

    /// <summary>停止行のステップ イン候補（先頭フレーム文脈）を取得する。停止していなければ空。</summary>
    public Task<IReadOnlyList<DebugStepInTarget>> GetStepInTargetsAsync()
        => _session.IsStopped && _inspection.SelectedFrame is { } f
            ? _debug.GetStepInTargetsAsync(f.Id)
            : Task.FromResult((IReadOnlyList<DebugStepInTarget>)Array.Empty<DebugStepInTarget>());

    /// <summary>指定の候補へステップ インする。</summary>
    public Task StepIntoTargetAsync(DebugStepInTarget target) => _debug.StepInTargetAsync(target.Id);
}
