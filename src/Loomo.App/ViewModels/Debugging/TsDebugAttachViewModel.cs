using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Node.js プロセスへのアタッチ（<c>node --inspect</c> のデバッグポートへ接続）を扱うサブ ViewModel。
/// 実行中の node プロセスを列挙して <c>--inspect</c> のポートを推定し（<see cref="TsNodeProcessEnumerator"/>）、
/// 選択またはポート手入力でアタッチする。「アタッチ」は必ず新しいセッションを作る。停止しても対象プロセスは
/// 終了しない（デタッチのみ）。</summary>
public sealed partial class TsDebugAttachViewModel : ObservableObject
{
    private readonly TsDebugViewModel _manager;
    private readonly ITerminalService _terminal;

    /// <summary>アタッチパネルを開いているか。</summary>
    [ObservableProperty] private bool _showAttach;

    /// <summary>接続先デバッグポートの入力値（既定 9229 = <c>node --inspect</c> の既定）。
    /// 一覧で選択するとそのポートが入る（手入力での上書きも可）。</summary>
    [ObservableProperty] private string _attachPort = "9229";

    /// <summary>プロセス一覧を取得中か（更新ボタンの無効化に使う）。</summary>
    [ObservableProperty] private bool _isEnumeratingProcesses;

    /// <summary>選択中の node プロセス（インスペクタポート付きならポート欄へ反映）。</summary>
    [ObservableProperty] private TsNodeProcessViewModel? _selectedProcess;

    /// <summary>実行中の node プロセス一覧（ポート検出済みが先）。</summary>
    public ObservableCollection<TsNodeProcessViewModel> Processes { get; } = new();

    /// <summary>一覧を一度でも取得したか（「該当なし」案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasEnumerated;

    /// <summary>セッションごとの再アタッチ用に、最後にアタッチしたポートを覚えておく（Restart 用）。</summary>
    internal int? LastAttachPort { get; private set; }

    internal TsDebugAttachViewModel(TsDebugViewModel manager, ITerminalService terminal)
    {
        _manager = manager;
        _terminal = terminal;
        _manager.SessionStateChanged += () => AttachCommand.NotifyCanExecuteChanged();
    }

    /// <summary>アタッチパネルの開閉。開くときに（未取得なら）プロセス一覧を読み込む。</summary>
    [RelayCommand]
    private async Task ToggleAttach()
    {
        ShowAttach = !ShowAttach;
        if (ShowAttach && !HasEnumerated)
            await RefreshProcessesAsync();
    }

    private bool CanRefreshProcesses() => !IsEnumeratingProcesses;

    /// <summary>node プロセスを列挙し直す（WMI 経由なのでバックグラウンド）。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshProcesses))]
    private async Task RefreshProcesses() => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        IsEnumeratingProcesses = true;
        try
        {
            var found = await TsNodeProcessEnumerator.EnumerateAsync(_terminal);
            Processes.Clear();
            foreach (var p in found) Processes.Add(p);
            HasEnumerated = true;
        }
        finally { IsEnumeratingProcesses = false; }
    }

    partial void OnSelectedProcessChanged(TsNodeProcessViewModel? value)
    {
        if (value is { InspectPort: { } port }) AttachPort = port.ToString();
        AttachCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEnumeratingProcessesChanged(bool value) => RefreshProcessesCommand.NotifyCanExecuteChanged();

    private bool CanAttach() => !_manager.IsTaskRunning;

    /// <summary>入力されたポート（一覧選択で自動反映）へ、新しいセッションでアタッチする。</summary>
    [RelayCommand(CanExecute = nameof(CanAttach))]
    private async Task Attach()
    {
        if (!int.TryParse(AttachPort?.Trim(), out var port) || port is <= 0 or >= 65536)
        {
            _manager.Append(DebugOutputCategory.Important, $"デバッグポートが不正です: {AttachPort}");
            return;
        }

        _manager.Refresh();
        if (_manager.IsAdapterMissing)
        {
            _manager.StatusMessage = "アダプタ未導入";
            _manager.Append(DebugOutputCategory.Important,
                "デバッグアダプタ（vscode-js-debug）または Node.js が未導入です。下のバーから導入できます。");
            return;
        }

        ShowAttach = false;
        LastAttachPort = port;
        var session = _manager.CreateSession($"attach :{port}", DebugSessionKind.Attach);
        await AttachIntoAsync(session, port);
    }

    /// <summary>同じポートへ、既存セッション（同じタブ）で再アタッチする（Restart 用）。</summary>
    internal async Task RelaunchIntoAsync(DebugSessionViewModel session)
    {
        if (LastAttachPort is { } port) await AttachIntoAsync(session, port);
    }

    private async Task AttachIntoAsync(DebugSessionViewModel session, int port)
    {
        _manager.RequestOutput();  // 押下時に即「出力」へ
        var iSession = (IDebugSession)session;
        var token = iSession.BeginSession();
        try
        {
            await session.DebugService.AttachAsync(new DebugAttachConfig(0, $"port {port}", Port: port), token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            iSession.Append(DebugOutputCategory.Important, $"アタッチでエラー: {ex.Message}");
        }
    }
}
