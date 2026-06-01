using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定パネル（プロバイダ切替・モデル・APIキー等）の ViewModel。
/// 編集内容は共有の <see cref="AiSettings"/>（Singleton）へ書き戻し、保存時にファイルへ永続化する。
/// システムプロンプト・危険コマンド一覧などの長文項目は、狭いサイドバーではなく中央のエディタ領域で
/// 編集する（<see cref="IEditorService.OpenDocumentAsync"/>。保存=:w 時にコールバックで即時反映）。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly CopilotAuthService _copilotAuth;
    private readonly IEditorService _editor;
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
    [ObservableProperty] private string _status = "";

    // --- 安全設計（設計書 §10） ---
    [ObservableProperty] private bool _autoApprove;
    [ObservableProperty] private bool _restrictToWorkspaceRoot;

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

    public SettingsViewModel(AiSettings settings, AiSettingsStore store, CopilotAuthService copilotAuth,
        IEditorService editor)
    {
        _settings = settings;
        _store = store;
        _copilotAuth = copilotAuth;
        _editor = editor;
        _selectedProvider = settings.Provider;
        _loadedProvider = settings.Provider;
        _autoApprove = settings.Safety.AutoApprove;
        _restrictToWorkspaceRoot = settings.Safety.RestrictToWorkspaceRoot;
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

        // 安全設計を書き戻す（同一インスタンスなので即時反映される）
        _settings.Safety.AutoApprove = AutoApprove;
        _settings.Safety.RestrictToWorkspaceRoot = RestrictToWorkspaceRoot;
        // システムプロンプト・危険コマンド一覧はエディタでの保存（:w）時に即時反映するため、ここでは扱わない。

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

    /// <summary>システムプロンプトを中央のエディタペインで開く。保存（:w）で settings.json へ即時反映。</summary>
    [RelayCommand]
    private async Task EditSystemPromptAsync()
    {
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = "loomo-system-prompt.md",
            Content = _settings.SystemPrompt,
            OnSaved = text =>
            {
                _settings.SystemPrompt = text.Trim();
                PersistAndNotify("システムプロンプトを保存しました");
            }
        });
        Status = "システムプロンプトをエディタで開きました。編集して保存（:w）すると反映されます。";
    }

    /// <summary>危険コマンドのブロックリストを中央のエディタペインで開く。保存（:w）で settings.json へ即時反映。</summary>
    [RelayCommand]
    private async Task EditBlockedCommandsAsync()
    {
        const string header =
            "# ブロックする危険コマンド（run_command の照合に使用）\n" +
            "# ・1行に1つ、正規表現で記述します（大文字小文字は無視）。\n" +
            "# ・'#' で始まる行と空行は無視されます。\n" +
            "\n";
        var body = string.Join("\n", _settings.Safety.BlockedCommandPatterns);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = "loomo-blocked-commands.txt",
            Content = header + body,
            OnSaved = text =>
            {
                _settings.Safety.BlockedCommandPatterns = ParsePatterns(text);
                PersistAndNotify("危険コマンドのブロックリストを保存しました");
            }
        });
        Status = "危険コマンド一覧をエディタで開きました。編集して保存（:w）すると反映されます。";
    }

    /// <summary>エディタ保存コールバックから呼ぶ共通処理：永続化して結果を表示。</summary>
    private void PersistAndNotify(string message)
    {
        try
        {
            _store.Save(_settings);
            Status = $"{message} — {_store.FilePath}";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"保存に失敗しました: {ex.Message}";
        }
    }

    /// <summary>エディタの行テキストを正規表現パターンのリストへ変換（空行・コメント行を除外）。</summary>
    private static List<string> ParsePatterns(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .ToList();

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
