using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Core.Lsp;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// エディタで開いたファイルの拡張子に対応する言語サーバーが「未導入／未設定」のとき、エディタ上端に出す
/// 促しバーの ViewModel。判定は <see cref="LspManagementService.EvaluateForFile"/> に委ね、ここでは
/// 「今後表示しない（永続）」「このセッションでは閉じた（一時）」のフィルタと、各操作の橋渡しだけを担う。
/// </summary>
public sealed partial class LspPromptViewModel : ObservableObject
{
    private readonly LspManagementService _service;
    private readonly LoomoSettings _settings;
    private readonly SettingsStore _store;

    // このセッション中に「×」で閉じた拡張子（再起動までは抑止、永続はしない）。
    private readonly HashSet<string> _sessionDismissed = new(StringComparer.OrdinalIgnoreCase);

    private LspPromptInfo? _current;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _canInstall;

    /// <summary>「設定を開く」を押したときに、LSP 設定オーバーレイを開くよう Shell へ要求する。</summary>
    public event Action? OpenSettingsRequested;

    public LspPromptViewModel(LspManagementService service, LoomoSettings settings, SettingsStore store)
    {
        _service = service;
        _settings = settings;
        _store = store;
    }

    /// <summary>アクティブなエディタタブが変わった／ファイルを開いたときに呼ぶ。促すべきならバーを出す。</summary>
    public void EvaluateForFile(string? filePath) => Show(_service.EvaluateForFile(filePath));

    /// <summary>
    /// 抑止済み（「今後表示しない」／このセッションで閉じた）の促しを null に落とす**共有フィルタ**。
    /// 促しバーだけでなく EditorSupport の案内（<c>LspNoticeModel</c>）もこれを通す。以前は
    /// このフィルタが <see cref="Show"/> の中だけにあり、アウトライン側は素の判定結果を使っていたため
    /// 「今後表示しない」にしても案内が出続けていた。
    /// </summary>
    public LspPromptInfo? Filter(LspPromptInfo? info)
        => info is not null && IsDismissed(info.Extension) ? null : info;

    private bool IsDismissed(string extension) =>
        _sessionDismissed.Contains(extension) ||
        _settings.Lsp.DismissedPromptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 判定済みの結果でバーを更新する。Shell 側は 1 ファイル 1 回だけ判定して
    /// 促しバーとアウトラインの案内で同じ結果を使い回すので、その入口。
    /// </summary>
    public void Show(LspPromptInfo? info)
    {
        info = Filter(info);
        if (info is null)
        {
            Hide();
            return;
        }

        _current = info;
        Message = info.Message;
        CanInstall = info.Kind == LspPromptKind.NotInstalled && info.InstallCommand is not null;
        IsVisible = true;
    }

    private void Hide()
    {
        _current = null;
        IsVisible = false;
    }

    [RelayCommand]
    private void Install()
    {
        if (_current is null) return;
        var name = _current.DisplayName ?? "言語サーバー";
        if (_service.InstallForPrompt(_current))
            Message = $"{name} のインストールを開始しました（ターミナルを確認してください）。";
        else
            Message = "可視ターミナルが見つかりません。ターミナルを開いてから再度お試しください。";
        // インストールは時間がかかるので、バーはそのまま案内表示にして閉じる操作はユーザーに委ねる。
        CanInstall = false;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Hide();
        OpenSettingsRequested?.Invoke();
    }

    /// <summary>この拡張子では今後この促しを出さない（settings.json に永続化）。</summary>
    [RelayCommand]
    private void DismissForever()
    {
        if (_current is { } info)
        {
            // 永続化する値は正規化（先頭ドット付き・小文字）しておく。
            var ext = LspExtensions.NormalizeExt(info.Extension);
            if (!_settings.Lsp.DismissedPromptExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                _settings.Lsp.DismissedPromptExtensions.Add(ext);
                try { _store.Save(_settings); } catch { /* 保存失敗でもバーは閉じる */ }
            }
        }
        Hide();
    }

    /// <summary>今回だけ閉じる（このセッション中は同じ拡張子で再表示しない）。</summary>
    [RelayCommand]
    private void Close()
    {
        if (_current is { } info)
            _sessionDismissed.Add(info.Extension);
        Hide();
    }
}
