using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>実行中プロセスへのアタッチを扱うサブ ViewModel。一覧の列挙・絞り込みは全セッション共有の「入り口」だが、
/// 「アタッチ」は必ず<b>新しいセッション</b>を作る（既存セッションは止めない）。停止してもそのプロセスは終了しない
/// （デタッチのみ）。</summary>
public sealed partial class DebugAttachViewModel : ObservableObject
{
    private readonly DebugViewModel _manager;

    /// <summary>アタッチのプロセス選択パネルを開いているか。</summary>
    [ObservableProperty] private bool _showAttach;

    /// <summary>プロセス一覧の絞り込み文字列（名前・PID・タイトルに含むで照合）。</summary>
    [ObservableProperty] private string _processFilter = "";

    /// <summary>.NET（coreclr 検出）プロセスのみ表示するか。アタッチ可能なのは基本これらのみ。</summary>
    [ObservableProperty] private bool _dotnetOnly = true;

    /// <summary>プロセス一覧を取得中か（更新ボタンの無効化に使う）。</summary>
    [ObservableProperty] private bool _isEnumeratingProcesses;

    /// <summary>選択中のアタッチ対象プロセス。</summary>
    [ObservableProperty] private DebugProcessViewModel? _selectedProcess;

    /// <summary>絞り込み後に表示するプロセス一覧。</summary>
    public ObservableCollection<DebugProcessViewModel> Processes { get; } = new();

    /// <summary>最後に列挙した全プロセス（絞り込み前）。フィルタ変更時はこれを再フィルタする。</summary>
    private IReadOnlyList<DebugProcessViewModel> _allProcesses = Array.Empty<DebugProcessViewModel>();

    internal DebugAttachViewModel(DebugViewModel manager)
    {
        _manager = manager;
        _manager.SessionStateChanged += () => AttachCommand.NotifyCanExecuteChanged();
    }

    /// <summary>アタッチパネルの開閉。開くときに（未取得なら）プロセス一覧を読み込む。</summary>
    [RelayCommand]
    private async Task ToggleAttach()
    {
        ShowAttach = !ShowAttach;
        if (ShowAttach && _allProcesses.Count == 0)
            await RefreshProcessesAsync();
    }

    private bool CanAttach() => !_manager.IsTaskRunning && SelectedProcess is not null;

    /// <summary>選択中プロセスに、新しいセッションでアタッチする。</summary>
    [RelayCommand(CanExecute = nameof(CanAttach))]
    private async Task Attach()
    {
        if (SelectedProcess is { } proc) await AttachToNewSessionAsync(proc);
    }

    /// <summary>指定プロセスへ新しいセッションでアタッチする（コマンド本体）。</summary>
    public async Task AttachToNewSessionAsync(DebugProcessViewModel proc)
    {
        _manager.Refresh();
        if (_manager.IsAdapterMissing)
        {
            _manager.StatusMessage = "アダプタ未導入";
            _manager.Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        ShowAttach = false;
        var session = _manager.CreateSession($"{proc.Name} (PID {proc.Pid})", DebugSessionKind.Attach);
        session.AttachedProcess = proc;
        await AttachIntoAsync(session, proc);
    }

    /// <summary>同じプロセスへ、既存セッション（同じタブ）で再アタッチする（Restart 用）。</summary>
    internal async Task RelaunchIntoAsync(DebugSessionViewModel session, DebugProcessViewModel proc)
        => await AttachIntoAsync(session, proc);

    private async Task AttachIntoAsync(DebugSessionViewModel session, DebugProcessViewModel proc)
    {
        _manager.RequestOutput();  // 押下時に即「出力」へ
        var iSession = (IDebugSession)session;
        var token = iSession.BeginSession();
        try
        {
            await session.DebugService.AttachAsync(new DebugAttachConfig(proc.Pid, proc.Name), token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            iSession.Append(DebugOutputCategory.Important, $"アタッチでエラー: {ex.Message}");
        }
    }

    private bool CanRefreshProcesses() => !IsEnumeratingProcesses;

    /// <summary>実行中プロセスを列挙し直す。判定（coreclr ロード検出）が重いのでバックグラウンドで実行する。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshProcesses))]
    private async Task RefreshProcesses() => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        IsEnumeratingProcesses = true;
        try
        {
            _allProcesses = await Task.Run(DebugProcessEnumerator.Enumerate);
            ApplyFilter();
        }
        finally { IsEnumeratingProcesses = false; }
    }

    private void ApplyFilter()
    {
        IEnumerable<DebugProcessViewModel> q = _allProcesses;
        if (DotnetOnly) q = q.Where(p => p.IsManaged);
        var f = ProcessFilter?.Trim();
        if (!string.IsNullOrEmpty(f))
            q = q.Where(p =>
                p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(f) ||
                (p.Title?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));

        Processes.Clear();
        foreach (var p in q) Processes.Add(p);
    }

    partial void OnProcessFilterChanged(string value) => ApplyFilter();
    partial void OnDotnetOnlyChanged(bool value) => ApplyFilter();
    partial void OnSelectedProcessChanged(DebugProcessViewModel? value) => AttachCommand.NotifyCanExecuteChanged();
    partial void OnIsEnumeratingProcessesChanged(bool value) => RefreshProcessesCommand.NotifyCanExecuteChanged();
}
