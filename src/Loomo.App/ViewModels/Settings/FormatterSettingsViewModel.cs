using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Core.Formatting;
using sk0ya.Loomo.Services.Formatting;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 設定オーバーレイ「整形 (Formatter)」セクションの ViewModel。エディタの拡張子→CLI 対応表
/// （<c>FormatterRegistry</c>）を、Loomo のカタログ（導入コマンド・手順URL）と PATH 検出に重ねて
/// 一覧化し、インストール・適用/解除・カスタム追加/削除を提供する。割り当てそのものの永続化は
/// エディタ側が担うため、ここは表示と操作の橋渡しに徹する（言語サーバー設定の双子）。
/// </summary>
public sealed partial class FormatterSettingsViewModel : ObservableObject
{
    private readonly FormatterManagementService _service;

    public ObservableCollection<FormatterRowViewModel> Formatters { get; } = new();

    [ObservableProperty] private string _status = "";

    // 新規追加フォーム。
    [ObservableProperty] private string _newExtension = "";
    [ObservableProperty] private string _newExecutable = "";
    [ObservableProperty] private string _newArgs = "";

    public FormatterSettingsViewModel(FormatterManagementService service)
    {
        _service = service;
    }

    /// <summary>設定オーバーレイを開いたとき（およびインストール/適用後）に呼ぶ。一覧と導入状況を取り直す。</summary>
    public void Refresh()
    {
        Formatters.Clear();
        foreach (var row in _service.GetRows())
            Formatters.Add(new FormatterRowViewModel(row, _service, OnRowChanged, SetStatus));
    }

    private void OnRowChanged() => Refresh();
    private void SetStatus(string message) => Status = message;

    [RelayCommand]
    private void RefreshStatus()
    {
        Refresh();
        Status = "導入状況を更新しました。";
    }

    [RelayCommand]
    private void AddFormatter()
    {
        var ext = NewExtension.Trim();
        var exe = NewExecutable.Trim();
        if (ext.Length == 0 || exe.Length == 0)
        {
            Status = "拡張子と実行ファイルを入力してください（例: .cs / csharpier）。";
            return;
        }

        var args = NewArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _service.AddOrUpdate(ext, exe, args);
        NewExtension = "";
        NewExecutable = "";
        NewArgs = "";
        Refresh();
        Status = $"{FormatterRegistry.NormalizeExt(ext)} → {exe} を追加しました（:Format で使われます）。";
    }
}

/// <summary>整形フォーマッタ一覧の1行。インストール／適用／解除／手順を開く操作を持つ。</summary>
public sealed partial class FormatterRowViewModel : ObservableObject
{
    private readonly FormatterRow _row;
    private readonly FormatterManagementService _service;
    private readonly Action _refresh;
    private readonly Action<string> _setStatus;

    public FormatterRowViewModel(FormatterRow row, FormatterManagementService service,
        Action refresh, Action<string> setStatus)
    {
        _row = row;
        _service = service;
        _refresh = refresh;
        _setStatus = setStatus;
    }

    public string DisplayName => _row.DisplayName;
    public string ExtensionsLabel => _row.ExtensionsLabel;
    public string CommandText =>
        _row.Args.Length > 0 ? $"{_row.Executable} {string.Join(' ', _row.Args)}" : _row.Executable;

    public bool Installed => _row.Installed;
    public bool Configured => _row.Configured;
    public bool IsCustom => _row.IsCustom;

    /// <summary>状況バッジ文言（導入状況＋適用状況）。</summary>
    public string StatusBadge =>
        (Installed ? "導入済み" : "未導入") + (Configured ? "・適用中" : "");

    /// <summary>由来ラベル（既知/カスタム）。</summary>
    public string OriginLabel => IsCustom ? "カスタム" : "既知";

    /// <summary>インストールボタンを出すか（未導入・コマンドが判っている）。</summary>
    public bool CanInstall => !Installed && _row.InstallCommand is not null;

    /// <summary>導入手順URLがあるか。</summary>
    public bool HasDocs => _row.DocsUrl is not null;

    /// <summary>「適用」ボタンを出すか（カタログ行で未適用のとき）。</summary>
    public bool CanApply => !IsCustom && !Configured;

    /// <summary>「解除」ボタンを出すか（適用中のとき。カスタムは常に解除可）。</summary>
    public bool CanUnapply => Configured;

    /// <summary>解除ボタンの文言。</summary>
    public string UnapplyLabel => IsCustom ? "削除" : "解除";

    [RelayCommand]
    private void Install()
    {
        if (_row.InstallCommand is null) return;
        if (_service.RunInstall(_row.InstallCommand))
            _setStatus($"{DisplayName} のインストールを開始しました（ターミナルを確認。完了後に「状況を更新」）。");
        else
            _setStatus("可視ターミナルが見つかりません。ターミナルを開いてから再度お試しください。");
    }

    /// <summary>カタログのフォーマッタを対象拡張子へ割り当てる。</summary>
    [RelayCommand]
    private void Apply()
    {
        var info = FormatterCatalog.ByExecutable(_row.Executable);
        if (info is null) return;
        _service.Apply(info);
        _setStatus($"{DisplayName} を {info.Extensions.Length} 拡張子に適用しました（:Format で使われます）。");
        _refresh();
    }

    /// <summary>適用中の割り当てを外す（カスタム行はその拡張子の登録を削除）。</summary>
    [RelayCommand]
    private void Unapply()
    {
        if (IsCustom)
        {
            if (_service.Remove(_row.Key))
            {
                _setStatus($"{_row.Key} の整形設定を削除しました。");
                _refresh();
            }
            return;
        }

        var info = FormatterCatalog.ByExecutable(_row.Executable);
        if (info is null) return;
        _service.Unapply(info);
        _setStatus($"{DisplayName} の割り当てを解除しました。");
        _refresh();
    }

    [RelayCommand]
    private void OpenDocs()
    {
        if (_row.DocsUrl is null) return;
        try { Process.Start(new ProcessStartInfo(_row.DocsUrl) { UseShellExecute = true }); }
        catch { /* 既定ブラウザが開けない環境では無視 */ }
    }
}
