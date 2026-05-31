using System;
using System.Collections.Generic;
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

    /// <summary>APIキー欄を表示するか（Stub は不要）。</summary>
    public bool ShowApiKey => SelectedProvider is AiProvider.Claude or AiProvider.OpenAI or AiProvider.Copilot;

    /// <summary>BaseUrl 欄を表示するか（OpenAI互換 / ローカルLLM）。</summary>
    public bool ShowBaseUrl => SelectedProvider is AiProvider.OpenAI or AiProvider.Local;

    /// <summary>モデル名・トークン上限を表示するか（Stub は不要）。</summary>
    public bool ShowModel => SelectedProvider != AiProvider.Stub;

    public SettingsViewModel(AiSettings settings, AiSettingsStore store)
    {
        _settings = settings;
        _store = store;
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
}
