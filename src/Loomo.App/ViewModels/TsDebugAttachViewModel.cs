using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Node.js プロセスへのアタッチ（<c>node --inspect</c> のデバッグポートへ接続）を扱うサブ ViewModel。
/// dotnet 側（<see cref="DebugAttachViewModel"/>＝プロセス列挙）と違い、v1 は<b>ポート番号指定</b>のみ
/// （--inspect の既定は 9229）。「アタッチ」は必ず新しいセッションを作る。停止しても対象プロセスは
/// 終了しない（デタッチのみ）。</summary>
public sealed partial class TsDebugAttachViewModel : ObservableObject
{
    private readonly TsDebugViewModel _manager;

    /// <summary>アタッチパネルを開いているか。</summary>
    [ObservableProperty] private bool _showAttach;

    /// <summary>接続先デバッグポートの入力値（既定 9229 = <c>node --inspect</c> の既定）。</summary>
    [ObservableProperty] private string _attachPort = "9229";

    /// <summary>セッションごとの再アタッチ用に、最後にアタッチしたポートを覚えておく（Restart 用）。</summary>
    internal int? LastAttachPort { get; private set; }

    internal TsDebugAttachViewModel(TsDebugViewModel manager)
    {
        _manager = manager;
        _manager.SessionStateChanged += () => AttachCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleAttach() => ShowAttach = !ShowAttach;

    private bool CanAttach() => !_manager.IsTaskRunning;

    /// <summary>入力されたポートへ、新しいセッションでアタッチする。</summary>
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
