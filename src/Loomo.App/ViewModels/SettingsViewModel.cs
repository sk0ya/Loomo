using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly ModelCatalogService _modelCatalog;
    private CancellationTokenSource? _signInCts;
    private CancellationTokenSource? _fetchModelsCts;

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

    /// <summary>エンドポイントから取得した利用可能モデルの一覧（選択肢）。</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>モデル一覧を取得中か。</summary>
    [ObservableProperty] private bool _isFetchingModels;

    /// <summary>モデル選択ドロップダウンを開いているか（取得完了時に自動で開く）。</summary>
    [ObservableProperty] private bool _modelDropDownOpen;

    /// <summary>Copilot サインインの進捗・状態表示。</summary>
    [ObservableProperty] private string _copilotStatus = "";
    [ObservableProperty] private bool _isSigningIn;

    /// <summary>APIキー欄を表示するか（手入力するのは Claude / OpenAI のみ。Copilot はサインイン）。</summary>
    public bool ShowApiKey => SelectedProvider is AiProvider.Claude or AiProvider.OpenAI;

    /// <summary>BaseUrl 欄を表示するか（OpenAI互換 / ローカルLLM）。</summary>
    public bool ShowBaseUrl => SelectedProvider is AiProvider.OpenAI or AiProvider.Local;

    /// <summary>モデル名・トークン上限を表示するか（Stub は不要）。</summary>
    public bool ShowModel => SelectedProvider != AiProvider.Stub;

    /// <summary>モデル一覧の自動取得に対応するプロバイダか（OpenAI互換 / ローカルLLM）。</summary>
    public bool CanFetchModels => ModelCatalogService.Supports(SelectedProvider);

    /// <summary>Copilot のサインインUIを表示するか。</summary>
    public bool ShowCopilotAuth => SelectedProvider == AiProvider.Copilot;

    /// <summary>Copilot に既にサインイン済みか（GitHub トークン保持）。</summary>
    public bool IsCopilotSignedIn => !string.IsNullOrWhiteSpace(_settings.Copilot.ApiKey);

    public SettingsViewModel(AiSettings settings, AiSettingsStore store, CopilotAuthService copilotAuth,
        IEditorService editor, ModelCatalogService modelCatalog)
    {
        _settings = settings;
        _store = store;
        _copilotAuth = copilotAuth;
        _editor = editor;
        _modelCatalog = modelCatalog;
        _selectedProvider = settings.Provider;
        _loadedProvider = settings.Provider;
        _autoApprove = settings.Safety.AutoApprove;
        _restrictToWorkspaceRoot = settings.Safety.RestrictToWorkspaceRoot;
        LoadProviderFields(settings.Provider);
        // 初期取得は UI スレッドのディスパッチャ経由で行う。コンストラクタ時点では
        // SynchronizationContext が未確立のことがあり、await 継続が別スレッドで走ると
        // バインド済み ObservableCollection の更新が失敗するため。アプリ未起動（テスト等）では no-op。
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => TryAutoFetchModels(autoOpen: false)));
    }

    /// <summary>タイトルバーのクイック切替に追従して、設定パネルの選択プロバイダを合わせる。
    /// <see cref="OnSelectedProviderChanged"/> がフィールドの退避・読込を行う（保存は伴わない）。</summary>
    public void SyncProvider(AiProvider provider)
    {
        if (SelectedProvider != provider) SelectedProvider = provider;
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
        OnPropertyChanged(nameof(CanFetchModels));
        // 進行中の取得を中止（切替後のプロバイダに前プロバイダの結果が紛れ込むのを防ぐ）。
        // 非対応プロバイダへ切り替えた場合は再取得しないため、ここで必ず止める必要がある。
        _fetchModelsCts?.Cancel();
        AvailableModels.Clear(); // プロバイダごとにモデル候補は異なるためクリア
        TryAutoFetchModels(autoOpen: true); // プロバイダ切替は操作起点なので取得後に候補を開いて見せる
    }

    /// <summary>対応プロバイダなら（OpenAI は鍵があるときのみ）モデル一覧を自動取得する。</summary>
    private void TryAutoFetchModels(bool autoOpen)
    {
        if (!CanFetchModels) return;
        // OpenAI は鍵未設定だと 401 になるだけなので、自動取得では鍵があるときに限る（ローカルは鍵不要）。
        if (SelectedProvider == AiProvider.OpenAI && string.IsNullOrWhiteSpace(ApiKey)) return;
        _ = FetchModelsCoreAsync(autoOpen);
    }

    private void LoadProviderFields(AiProvider provider)
    {
        var cfg = _settings.ConfigFor(provider);
        Model = cfg.Model;
        // Copilot のトークンは GitHub サインインで取得・保持するため、共有の APIキー欄には載せない
        // （載せると Copilot 切替時にこの空欄が CommitFieldsTo でトークンを上書き消去してしまう）。
        ApiKey = provider == AiProvider.Copilot ? "" : (cfg.ApiKey ?? "");
        BaseUrl = cfg.BaseUrl ?? "";
        MaxTokens = cfg.MaxTokens;
    }

    private void CommitFieldsTo(AiProvider provider)
    {
        if (provider == AiProvider.Stub) return; // Stub に編集項目は無い
        var cfg = _settings.ConfigFor(provider);
        cfg.Model = Model.Trim();
        // Copilot の ApiKey は SignInCopilot/SignOutCopilot が専管する。ここで空欄により上書きしない。
        if (provider != AiProvider.Copilot)
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

    /// <summary>手動の「再取得」ボタン。取得後に候補ドロップダウンを開いて見せる。</summary>
    [RelayCommand]
    private Task FetchModelsAsync() => FetchModelsCoreAsync(autoOpen: true);

    /// <summary>現在のエンドポイント（編集中の BaseUrl / APIキー）から利用可能なモデル一覧を取得し、選択肢に反映する。
    /// <paramref name="autoOpen"/> が true なら取得成功後にドロップダウンを自動で開く。</summary>
    private async Task FetchModelsCoreAsync(bool autoOpen)
    {
        if (!CanFetchModels) return;

        // 進行中の取得を中止し、最新の要求で置き換える（取り違え・古い結果の反映を防ぐ）。
        // 各呼び出しは自分の CTS を所有し、自分の finally で破棄する。共有状態
        // （IsFetchingModels / _fetchModelsCts）は最新の取得だけがリセットする。
        _fetchModelsCts?.Cancel();
        var cts = _fetchModelsCts = new CancellationTokenSource();
        var provider = SelectedProvider;
        IsFetchingModels = true;
        try
        {
            Status = "モデル一覧を取得しています…";
            var models = await _modelCatalog.FetchAsync(
                provider,
                baseUrlOverride: BaseUrl,
                apiKeyOverride: ApiKey,
                ct: cts.Token);

            // 取得中に中止／プロバイダ変更があれば結果は破棄する。
            if (cts.IsCancellationRequested || provider != SelectedProvider) return;

            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);

            if (AvailableModels.Count == 0)
            {
                Status = "モデルが見つかりませんでした。エンドポイントが起動しているか確認してください。";
            }
            else
            {
                // 既存の選択（保存済み・入力中のモデル名）は勝手に変更しない。未選択のときだけ
                // 先頭候補で補完する。候補との不一致（例: "llama3.1" と Ollama の "llama3.1:latest"）
                // で保存済みモデルを上書きしないため、autoOpen でも非空の値はそのまま残す。
                if (string.IsNullOrWhiteSpace(Model))
                    Model = AvailableModels[0];
                Status = $"{AvailableModels.Count} 件のモデルを取得しました。";
                if (autoOpen) ModelDropDownOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
            // より新しい取得に置き換えられた／キャンセルされた。Status は上書きしない。
        }
        catch (Exception ex)
        {
            if (provider == SelectedProvider)
                Status = $"モデル取得に失敗しました: {ex.Message}";
        }
        finally
        {
            if (_fetchModelsCts == cts) // この取得が最新のときだけ共有状態を片付ける
            {
                IsFetchingModels = false;
                _fetchModelsCts = null;
            }
            cts.Dispose();
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
