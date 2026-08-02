using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 設定オーバーレイ「言語サーバー (LSP)」セクションの ViewModel。エディタの拡張子→サーバー対応表
/// （<c>LspServerTable</c>）を一覧化し、各サーバーの導入状況（PATH 検出）を見せ、見えるターミナルでの
/// インストール・追加・削除・既定復帰を提供する。サーバー対応そのものの永続化はエディタ側が担うため、
/// ここは表示と操作の橋渡しに徹する。
/// </summary>
public sealed partial class LspSettingsViewModel : ObservableObject
{
    private readonly LspManagementService _service;
    private readonly LspWorkspaceService? _workspace;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<LspServerRowViewModel> Servers { get; } = new();

    [ObservableProperty] private string _status = "";

    // 新規追加フォーム。
    [ObservableProperty] private string _newExtension = "";
    [ObservableProperty] private string _newExecutable = "";
    [ObservableProperty] private string _newArgs = "";

    public LspSettingsViewModel(LspManagementService service, LspWorkspaceService? workspace = null)
    {
        _service = service;
        _workspace = workspace;
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (_workspace is not null)
            _workspace.ServerStateChanged += OnServerStateChanged;
    }

    /// <summary>設定オーバーレイを開いたとき（およびインストール後）に呼ぶ。一覧と導入状況を取り直す。</summary>
    public void Refresh()
    {
        Servers.Clear();
        var runtime = _workspace?.ServerStatuses
            .GroupBy(s => s.Executable, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.State).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in _service.GetRows())
        {
            LspServerRuntimeStatus? status = null;
            runtime?.TryGetValue(row.Executable, out status);
            Servers.Add(new LspServerRowViewModel(row, _service, OnRowChanged, SetStatus, status, _workspace));
        }
    }

    private void OnRowChanged() => Refresh();
    private void OnServerStateChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _ = _dispatcher.InvokeAsync(Refresh);
    }
    private void SetStatus(string message) => Status = message;

    [RelayCommand]
    private void RefreshStatus()
    {
        Refresh();
        Status = "導入状況を更新しました。";
    }

    [RelayCommand]
    private void AddServer()
    {
        var ext = NewExtension.Trim();
        var exe = NewExecutable.Trim();
        if (ext.Length == 0 || exe.Length == 0)
        {
            Status = "拡張子と実行ファイルを入力してください（例: .zig / zls）。";
            return;
        }

        var args = NewArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _service.AddOrUpdate(ext, exe, args);
        NewExtension = "";
        NewExecutable = "";
        NewArgs = "";
        Refresh();
        Status = $"{LspExtensions.NormalizeExt(ext)} → {exe} を追加しました。";
    }
}

/// <summary>LSP 設定一覧の1行。サーバーの表示と、インストール／削除／既定復帰／手順を開く操作を持つ。</summary>
public sealed partial class LspServerRowViewModel : ObservableObject
{
    private readonly LspServerRow _row;
    private readonly LspManagementService _service;
    private readonly Action _refresh;
    private readonly Action<string> _setStatus;

    public LspServerRowViewModel(LspServerRow row, LspManagementService service,
        Action refresh, Action<string> setStatus, LspServerRuntimeStatus? runtime = null,
        LspWorkspaceService? workspace = null)
    {
        _row = row;
        _service = service;
        _refresh = refresh;
        _setStatus = setStatus;
        Runtime = runtime;
        Workspace = workspace;
    }

    public string Extension => _row.Extension;
    public string DisplayName => _row.DisplayName;
    public string Executable => _row.Executable;
    public string CommandText =>
        _row.Args.Length > 0 ? $"{_row.Executable} {string.Join(' ', _row.Args)}" : _row.Executable;

    public bool Installed => _row.Installed;
    public bool IsRemoved => _row.Origin == LspServerOrigin.Removed;
    public bool IsCustom => _row.Origin == LspServerOrigin.Custom;
    public LspServerRuntimeStatus? Runtime { get; }
    private LspWorkspaceService? Workspace { get; }
    public bool CanRetry => Runtime is not null;

    /// <summary>状況バッジ文言。</summary>
    public string StatusBadge => IsRemoved ? "未構成" : !Installed ? "未導入" : Runtime?.State switch
    {
        LspServerRuntimeState.Starting => "起動中",
        LspServerRuntimeState.Initializing => "初期化中",
        LspServerRuntimeState.ProjectLoading => "プロジェクト読込中",
        LspServerRuntimeState.Ready => "ready",
        LspServerRuntimeState.Reconnecting => "再接続中",
        LspServerRuntimeState.Stopped => "停止",
        LspServerRuntimeState.Failed => "失敗",
        _ => "導入済み（停止）",
    };

    public string RuntimeDetail => Runtime is null ? "" :
        $"root: {Runtime.Root}" +
        (Runtime.ReconnectAttempt > 0 ? $" / 再試行 {Runtime.ReconnectAttempt}" : "") +
        (string.IsNullOrWhiteSpace(Runtime.LastError) ? "" : $" / {Runtime.LastError}");

    /// <summary>由来ラベル（組み込み/カスタム）。</summary>
    public string OriginLabel => _row.Origin switch
    {
        LspServerOrigin.Custom => "カスタム",
        LspServerOrigin.Removed => "無効化",
        _ => "組み込み",
    };

    /// <summary>インストールボタンを出すか（未導入・無効でなく・コマンドが判っている）。</summary>
    public bool CanInstall => !Installed && !IsRemoved && _row.InstallCommand is not null;

    /// <summary>導入手順URLがあるか。</summary>
    public bool HasDocs => _row.DocsUrl is not null;

    /// <summary>削除/無効化ボタンを出すか（無効化済みの行には出さない）。</summary>
    public bool CanRemove => !IsRemoved;

    /// <summary>既定復帰/有効化ボタンを出すか（カスタム上書き or 無効化済みのとき）。</summary>
    public bool CanReset => IsCustom || IsRemoved;

    /// <summary>削除ボタンの文言（カスタムは削除・組み込みは無効化）。</summary>
    public string RemoveLabel => IsCustom ? "削除" : "無効化";

    /// <summary>復帰ボタンの文言（無効化を戻すなら有効化・上書きを戻すなら既定）。</summary>
    public string ResetLabel => IsRemoved ? "有効化" : "既定に戻す";

    [RelayCommand]
    private void Install()
    {
        if (_row.InstallCommand is null) return;
        if (_service.RunInstall(_row.InstallCommand))
            _setStatus($"{DisplayName} のインストールを開始しました（ターミナルを確認。完了後に「状況を更新」）。");
        else
            _setStatus("可視ターミナルが見つかりません。ターミナルを開いてから再度お試しください。");
    }

    /// <summary>カスタムは削除、組み込みは無効化。</summary>
    [RelayCommand]
    private void Remove()
    {
        if (_service.Remove(_row.Extension))
        {
            _setStatus($"{_row.Extension} を{(IsCustom ? "削除" : "無効化")}しました。");
            _refresh();
        }
    }

    /// <summary>無効化／上書きを取り消して組み込み既定へ戻す。</summary>
    [RelayCommand]
    private void Reset()
    {
        if (_service.Reset(_row.Extension))
        {
            _setStatus($"{_row.Extension} を組み込み既定に戻しました。");
            _refresh();
        }
    }

    [RelayCommand]
    private void OpenDocs()
    {
        if (_row.DocsUrl is null) return;
        try { Process.Start(new ProcessStartInfo(_row.DocsUrl) { UseShellExecute = true }); }
        catch { /* 既定ブラウザが開けない環境では無視 */ }
    }

    [RelayCommand]
    private void Retry()
    {
        if (Workspace?.RestartServer(Executable) == true)
        {
            _setStatus($"{DisplayName} を再起動しています。開いている文書は自動的に再送されます。");
            _refresh();
        }
        else
            _setStatus($"{DisplayName} は停止中です。対象ファイルを開くと起動します。");
    }
}
