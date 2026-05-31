using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定パネル（プロバイダ切替・モデル・APIキー等）の ViewModel。
/// 編集内容は共有の <see cref="AiSettings"/>（Singleton）へ書き戻し、保存時にファイルへ永続化する。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly CopilotAuthService _copilotAuth;
    private CancellationTokenSource? _signInCts;

    /// <summary>現在フィールドに読み込まれているプロバイダ。切替時に旧プロバイダへコミットするため保持。</summary>
    private AiProvider _loadedProvider;

    /// <summary>設定が保存されたときに通知（AIバーのプロバイダ表示更新などに使う）。</summary>
    public event Action? Saved;

    public IReadOnlyList<AiProvider> Providers { get; } =
        (AiProvider[])Enum.GetValues(typeof(AiProvider));

    [ObservableProperty] private AiProvider _selectedProvider;
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private int _maxTokens;
    [ObservableProperty] private string _systemPrompt = "";
    [ObservableProperty] private string _status = "";

    /// <summary>Copilot サインインの進捗・状態表示。</summary>
    [ObservableProperty] private string _copilotStatus = "";
    [ObservableProperty] private bool _isSigningIn;

    /// <summary>APIキー欄を表示するか（手入力するのは Claude / OpenAI のみ。Copilot はサインイン）。</summary>
    public bool ShowApiKey => SelectedProvider is AiProvider.Claude or AiProvider.OpenAI;

    /// <summary>BaseUrl 欄を表示するか（OpenAI互換 / ローカルLLM）。</summary>
    public bool ShowBaseUrl => SelectedProvider is AiProvider.OpenAI or AiProvider.Local;

    /// <summary>モデル名・トークン上限を表示するか（Stub は不要）。</summary>
    public bool ShowModel => SelectedProvider != AiProvider.Stub;

    /// <summary>Copilot のサインインUIを表示するか。</summary>
    public bool ShowCopilotAuth => SelectedProvider == AiProvider.Copilot;

    /// <summary>Copilot に既にサインイン済みか（GitHub トークン保持）。</summary>
    public bool IsCopilotSignedIn => !string.IsNullOrWhiteSpace(_settings.Copilot.ApiKey);

    public SettingsViewModel(AiSettings settings, AiSettingsStore store, CopilotAuthService copilotAuth)
    {
        _settings = settings;
        _store = store;
        _copilotAuth = copilotAuth;
        _systemPrompt = settings.SystemPrompt;
        _selectedProvider = settings.Provider;
        _loadedProvider = settings.Provider;
        LoadProviderFields(settings.Provider);
    }

    partial void OnSelectedProviderChanged(AiProvider value)
    {
        CommitFieldsTo(_loadedProvider);   // 直前プロバイダの編集をメモリへ退避
        LoadProviderFields(value);
        _loadedProvider = value;
        OnPropertyChanged(nameof(ShowApiKey));
        OnPropertyChanged(nameof(ShowBaseUrl));
        OnPropertyChanged(nameof(ShowModel));
        OnPropertyChanged(nameof(ShowCopilotAuth));
        OnPropertyChanged(nameof(IsCopilotSignedIn));
    }

    private void LoadProviderFields(AiProvider provider)
    {
        var cfg = _settings.ConfigFor(provider);
        Model = cfg.Model;
        ApiKey = cfg.ApiKey ?? "";
        BaseUrl = cfg.BaseUrl ?? "";
        MaxTokens = cfg.MaxTokens;
    }

    private void CommitFieldsTo(AiProvider provider)
    {
        if (provider == AiProvider.Stub) return; // Stub に編集項目は無い
        var cfg = _settings.ConfigFor(provider);
        cfg.Model = Model.Trim();
        cfg.ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        cfg.BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim();
        cfg.MaxTokens = MaxTokens > 0 ? MaxTokens : 4096;
    }

    [RelayCommand]
    private void Save()
    {
        CommitFieldsTo(SelectedProvider);
        _settings.Provider = SelectedProvider;
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            _settings.SystemPrompt = SystemPrompt.Trim();

        try
        {
            _store.Save(_settings);
            Status = $"保存しました — {_store.FilePath}";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"保存に失敗しました: {ex.Message}";
        }
    }

    /// <summary>GitHub デバイス認証で Copilot 用トークンを取得して保存する。</summary>
    [RelayCommand]
    private async Task SignInCopilotAsync()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        _signInCts = new CancellationTokenSource();
        try
        {
            var info = await _copilotAuth.RequestDeviceCodeAsync(_signInCts.Token);

            // ユーザーコードをクリップボードへ入れ、ブラウザで認証ページを開く
            TrySetClipboard(info.UserCode);
            TryOpenBrowser(info.VerificationUri);
            CopilotStatus =
                $"コード {info.UserCode} を入力してください（クリップボードにコピー済み）。\n" +
                $"ブラウザ: {info.VerificationUri}\n承認を待っています…";

            var token = await _copilotAuth.PollForAccessTokenAsync(info, _signInCts.Token);

            _settings.Copilot.ApiKey = token;
            _store.Save(_settings);
            CopilotStatus = "サインインしました。Copilot が利用できます。";
            OnPropertyChanged(nameof(IsCopilotSignedIn));
            Saved?.Invoke();
        }
        catch (OperationCanceledException)
        {
            CopilotStatus = "サインインをキャンセルしました。";
        }
        catch (Exception ex)
        {
            CopilotStatus = $"サインインに失敗しました: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    /// <summary>保存済み Copilot トークンを破棄する。</summary>
    [RelayCommand]
    private void SignOutCopilot()
    {
        _settings.Copilot.ApiKey = null;
        _store.Save(_settings);
        CopilotStatus = "サインアウトしました。";
        OnPropertyChanged(nameof(IsCopilotSignedIn));
    }

    private static void TryOpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 開けなくても URL は表示済み */ }
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* クリップボード使用不可は無視 */ }
    }
}
