using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定パネル（ローカルモデル等）の ViewModel。
/// 編集内容は共有の <see cref="AiSettings"/>（Singleton）へ書き戻し、保存時にファイルへ永続化する。
/// 危険コマンド一覧などの長文項目は、狭いサイドバーではなく中央のエディタ領域で
/// 編集する（<see cref="IEditorService.OpenDocumentAsync"/>。保存=:w 時にコールバックで即時反映）。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly ModelCatalogService _modelCatalog;
    private readonly ModelDownloadService _modelDownload;
    private readonly Services.IAiWarmup _warmup;
    private readonly BlockedCommandsHandler _blockedCommands;
    private readonly SettingsPersistenceHandler _persistence;
    private readonly SettingsModelChoiceMapper _choiceMapper;
    private readonly ModelFolderPicker _modelFolderPicker;
    private CancellationTokenSource? _fetchModelsCts;
    private CancellationTokenSource? _downloadCts;
    // 初期ロード中の代入で自動保存（Persist）が走らないようにするためのガード。
    private bool _suppressPersist = true;

    public event Action? Saved;

    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private int _maxTokens;

    /// <summary>AIウォームアップを有効にするか。ONにすると起動時／ワークスペース確定時に
    /// モデルとプロンプトを事前ロードして初回ターンを速くする（実行中はAI指示を受け付けない）。</summary>
    [ObservableProperty] private bool _warmupEnabled;

    [ObservableProperty] private bool _vimEnabled;
    [ObservableProperty] private bool _highlightWhitespace;
    [ObservableProperty] private bool _showLineNumbers;
    [ObservableProperty] private bool _relativeLineNumbers;
    [ObservableProperty] private bool _highlightCurrentLine;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _showMinimap;
    [ObservableProperty] private bool _showIndentGuides;
    [ObservableProperty] private bool _autoClosePairs;
    [ObservableProperty] private int _tabWidth;
    [ObservableProperty] private bool _useSpacesForTab;
    [ObservableProperty] private string _imagePasteDirectory = "";
    [ObservableProperty] private string _imagePasteFileName = "";
    [ObservableProperty] private string _imagePasteAltText = "";
    [ObservableProperty] private string _status = "";

    // --- 安全設計（設計書 §10） ---
    [ObservableProperty] private bool _autoApprove;
    [ObservableProperty] private bool _restrictToWorkspaceRoot;

    /// <summary>ローカルに配置済みのモデルフォルダ名の一覧。</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    [ObservableProperty] private bool _isFetchingModels;

    /// <summary>モデル選択ドロップダウンを開いているか（XAML の IsDropDownOpen と双方向バインド）。</summary>
    [ObservableProperty] private bool _modelDropDownOpen;

    /// <summary>モデル選択欄に出す選択肢：ローカル配置済み＋未取得のカタログ候補を1本にまとめたもの。</summary>
    public ObservableCollection<ModelChoice> ModelChoices { get; } = new();

    /// <summary>現在の <see cref="Model"/> がローカルに配置済みか。false なら未ダウンロード
    /// （フォルダパス表示の代わりにダウンロード案内を出す）。</summary>
    [ObservableProperty] private bool _selectedModelIsInstalled = true;

    /// <summary>ダウンロード中か。</summary>
    [ObservableProperty] private bool _isDownloading;

    /// <summary>ダウンロード進捗（0–100、不明時は不定表示用に -1）。</summary>
    [ObservableProperty] private double _downloadProgress;

    /// <summary>ダウンロードボタンで取得する対象モデル。<see cref="Model"/> が未取得のカタログ候補を
    /// 指しているときに <see cref="RefreshModelChoices"/> が追従させる。既定は Qwen3-4B GGUF Q4_K_M。</summary>
    [ObservableProperty] private DownloadableModel _selectedDownloadModel =
        ModelDownloadService.Default;

    public SettingsViewModel(AiSettings settings, AiSettingsStore store,
        IEditorService editor, ModelCatalogService modelCatalog, ModelDownloadService modelDownload,
        Services.IAiWarmup warmup, ModelFolderPicker modelFolderPicker, BlockedCommandsHandler blockedCommands,
        SettingsPersistenceHandler persistence, SettingsModelChoiceMapper choiceMapper)
    {
        _settings = settings;
        _modelCatalog = modelCatalog;
        _modelDownload = modelDownload;
        _warmup = warmup;
        _blockedCommands = blockedCommands;
        _persistence = persistence;
        _choiceMapper = choiceMapper;
        _modelFolderPicker = modelFolderPicker;
        _autoApprove = settings.Safety.AutoApprove;
        _restrictToWorkspaceRoot = settings.Safety.RestrictToWorkspaceRoot;
        LoadLocalFields();
        RefreshModelChoices();
    }

    private void LoadLocalFields()
    {
        // 初期ロード中の代入で自動保存が走らないよう抑止する。
        _suppressPersist = true;
        var form = _persistence.Load();
        Model = form.Model; ModelPath = form.ModelPath; MaxTokens = form.MaxTokens;
        WarmupEnabled = form.WarmupEnabled; VimEnabled = form.VimEnabled;
        HighlightWhitespace = form.HighlightWhitespace; ShowLineNumbers = form.ShowLineNumbers;
        RelativeLineNumbers = form.RelativeLineNumbers; HighlightCurrentLine = form.HighlightCurrentLine;
        WordWrap = form.WordWrap; ShowMinimap = form.ShowMinimap; ShowIndentGuides = form.ShowIndentGuides;
        AutoClosePairs = form.AutoClosePairs; TabWidth = form.TabWidth; UseSpacesForTab = form.UseSpacesForTab;
        ImagePasteDirectory = form.ImagePasteDirectory; ImagePasteFileName = form.ImagePasteFileName;
        ImagePasteAltText = form.ImagePasteAltText;
        _suppressPersist = false;
    }

    /// <summary>配置済みモデルと未取得候補を1本の一覧にまとめ直す。</summary>
    private void RefreshModelChoices()
    {
        var mapped = _choiceMapper.Map(AvailableModels, Model);
        ModelChoices.Clear();
        foreach (var choice in mapped.Choices) ModelChoices.Add(choice);
        SelectedModelIsInstalled = mapped.SelectedIsInstalled;
        if (mapped.SelectedDownload is { } download) SelectedDownloadModel = download;
    }

    private void UpdateSelectedModelStatus()
    {
        var mapped = _choiceMapper.Map(AvailableModels, Model);
        SelectedModelIsInstalled = mapped.SelectedIsInstalled;
        if (mapped.SelectedDownload is { } download) SelectedDownloadModel = download;
    }

    // 「保存」ボタンは廃止。各項目の変更はその場で共有 AiSettings へ反映し、ファイルへ即永続化する。
    partial void OnModelChanged(string value)
    {
        // 一覧から選んだ（または入力した）モデル名に対応するローカルフォルダがあれば ModelPath を追従させる。
        if (!_suppressPersist)
        {
            var resolved = _modelCatalog.ResolvePath(value.Trim());
            if (!string.IsNullOrEmpty(resolved))
            {
                _suppressPersist = true;
                ModelPath = resolved;
                _suppressPersist = false;
            }
        }
        UpdateSelectedModelStatus();
        Persist();
    }
    partial void OnModelPathChanged(string value) => Persist();
    partial void OnMaxTokensChanged(int value) => Persist();
    partial void OnVimEnabledChanged(bool value) => Persist();
    partial void OnHighlightWhitespaceChanged(bool value) => Persist();
    partial void OnShowLineNumbersChanged(bool value) => Persist();
    partial void OnRelativeLineNumbersChanged(bool value) => Persist();
    partial void OnHighlightCurrentLineChanged(bool value) => Persist();
    partial void OnWordWrapChanged(bool value) => Persist();
    partial void OnShowMinimapChanged(bool value) => Persist();
    partial void OnShowIndentGuidesChanged(bool value) => Persist();
    partial void OnAutoClosePairsChanged(bool value) => Persist();
    partial void OnTabWidthChanged(int value) => Persist();
    partial void OnUseSpacesForTabChanged(bool value) => Persist();
    partial void OnImagePasteDirectoryChanged(string value) => Persist();
    partial void OnImagePasteFileNameChanged(string value) => Persist();
    partial void OnImagePasteAltTextChanged(string value) => Persist();
    partial void OnWarmupEnabledChanged(bool value)
    {
        if (_suppressPersist) return;
        Persist();
        // ONに切り替えたら、その場でウォームアップを開始して初回ターンを速くする。
        if (value) _warmup.RequestWarmup();
    }
    partial void OnAutoApproveChanged(bool value) => Persist();
    partial void OnRestrictToWorkspaceRootChanged(bool value) => Persist();

    /// <summary>変更を共有 <see cref="AiSettings"/> へ即時反映し、ファイルへ永続化する。
    /// 「保存」ボタンを廃した代わりに、各項目の変更時に自動で呼ばれる（初期ロード中は抑止）。
    /// 危険コマンド一覧はエディタの保存（:w）時に別途反映するため、ここでは扱わない。</summary>
    private void Persist()
    {
        if (_suppressPersist) return;

        var result = _persistence.Save(new SettingsFormState
        {
            Model = Model, ModelPath = ModelPath, MaxTokens = MaxTokens, WarmupEnabled = WarmupEnabled,
            VimEnabled = VimEnabled, HighlightWhitespace = HighlightWhitespace, ShowLineNumbers = ShowLineNumbers,
            RelativeLineNumbers = RelativeLineNumbers, HighlightCurrentLine = HighlightCurrentLine,
            WordWrap = WordWrap, ShowMinimap = ShowMinimap, ShowIndentGuides = ShowIndentGuides,
            AutoClosePairs = AutoClosePairs, TabWidth = TabWidth, UseSpacesForTab = UseSpacesForTab,
            ImagePasteDirectory = ImagePasteDirectory, ImagePasteFileName = ImagePasteFileName,
            ImagePasteAltText = ImagePasteAltText, AutoApprove = AutoApprove,
            RestrictToWorkspaceRoot = RestrictToWorkspaceRoot,
        });
        ApplyCommandResult(result);
    }

    /// <summary>設定パネルを開いたときに呼ぶ。ローカルのモデルフォルダを列挙し直す
    /// （専用の「再取得」ボタンは廃止し、パネルを開く＝最新化とした）。取得中なら二重起動しない。
    /// 一覧の内容が変わらなければコレクションには触れないので、選択中の Model は保持される
    /// （<see cref="FetchModelsCoreAsync"/>）。</summary>
    public void EnsureModelsLoaded()
    {
        if (IsFetchingModels) return;
        _ = FetchModelsCoreAsync();
    }

    /// <summary>ローカルに配置済みの GGUF モデルフォルダを列挙し、選択肢に反映する。
    /// 一覧の内容が前回と同じなら <see cref="AvailableModels"/> には触れない。これは編集可能な
    /// ComboBox の Text→Model 双方向バインドが、ItemsSource の Clear で空文字に巻き戻り、
    /// 選択中モデルが先頭候補へすり替わって自動保存・AIクライアント再解決を招くのを防ぐため。
    /// 内容が変わるときも、退避した選択（Model）を再追加後に復元する。</summary>
    private async Task FetchModelsCoreAsync()
    {
        // 進行中の取得を中止し、最新の要求で置き換える（取り違え・古い結果の反映を防ぐ）。
        // 各呼び出しは自分の CTS を所有し、自分の finally で破棄する。共有状態
        // （IsFetchingModels / _fetchModelsCts）は最新の取得だけがリセットする。
        _fetchModelsCts?.Cancel();
        var cts = _fetchModelsCts = new CancellationTokenSource();
        var provider = AiProvider.Local;
        IsFetchingModels = true;
        try
        {
            Status = "モデル一覧を取得しています…";
            var fetched = (await _modelCatalog.FetchAsync(
                provider,
                ct: cts.Token)).ToList();

            // 取得中に中止／プロバイダ変更があれば結果は破棄する。
            if (cts.IsCancellationRequested) return;

            // 内容が同じなら一覧は触らない（不要な Clear による選択リセット・自動保存を避ける）。
            if (!AvailableModels.SequenceEqual(fetched))
            {
                // Clear で編集可能コンボの Text が空に巻き戻り Model が消えることがあるため、
                // 退避した選択を再追加後に必ず復元する（候補に無くてもテキストとして保持）。
                var previous = Model;
                AvailableModels.Clear();
                foreach (var m in fetched) AvailableModels.Add(m);
                if (!string.IsNullOrWhiteSpace(previous))
                    Model = previous;
            }

            RefreshModelChoices();

            if (AvailableModels.Count == 0)
            {
                Status = "ローカルにモデルが見つかりませんでした。下の一覧からダウンロード、または「📂」で別の場所から追加してください。";
            }
            else
            {
                // 未選択のときだけ先頭候補で補完する（保存済み・選択中のモデルは上書きしない）。
                if (string.IsNullOrWhiteSpace(Model))
                    Model = AvailableModels[0];
                Status = $"{AvailableModels.Count} 件のモデルを取得しました。";
            }
        }
        catch (OperationCanceledException)
        {
            // より新しい取得に置き換えられた／キャンセルされた。Status は上書きしない。
        }
        catch (Exception ex)
        {
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

    /// <summary>モデルフォルダを手動で選ぶ（任意の場所に置いたモデル用）。ONNX（<c>genai_config.json</c> を含む）
    /// と GGUF（<c>*.gguf</c> を含む・llama.cpp バックエンド）の両方を受け付ける。GGUF はフォルダ内の
    /// <c>.gguf</c> ファイルパスを ModelPath に入れる（ルータが拡張子で振り分けるため。フォルダのまま入れると
    /// GGUF が ONNX 行きになり「genai_config.json が見つかりません」になる）。モデル名にはフォルダ名を流用する。</summary>
    [RelayCommand]
    private void BrowseModel()
    {
        var selection = _modelFolderPicker.Pick(ModelPath);
        if (selection is null)
        {
            return;
        }

        ModelPath = selection.ModelPath;              // OnModelPathChanged → Persist
        var name = selection.Name;
        if (!string.IsNullOrEmpty(name) && !AvailableModels.Contains(name))
            AvailableModels.Add(name);
        RefreshModelChoices();
        if (!string.IsNullOrEmpty(name))
            Model = name;
        Status = $"モデルフォルダを設定しました: {selection.Folder}";
    }

    /// <summary>選択中のモデル（GGUF・CPU）を Hugging Face からダウンロードして設定する。</summary>
    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (IsDownloading) return;
        var model = SelectedDownloadModel ?? ModelDownloadService.Default;
        _downloadCts?.Cancel();
        var cts = _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        try
        {
            Status = $"{model.DisplayName} をダウンロードしています…";
            var progress = new Progress<ModelDownloadService.Progress>(p =>
            {
                DownloadProgress = p.TotalBytes > 0 ? p.DownloadedBytes * 100.0 / p.TotalBytes : -1;
                var pct = p.TotalBytes > 0 ? $"{DownloadProgress:0}%" : $"{p.DownloadedBytes / (1024 * 1024)}MB";
                Status = $"ダウンロード中 ({p.FileIndex}/{p.FileCount}) {p.CurrentFile} — {pct}";
            });

            var dir = await _modelDownload.DownloadAsync(model, progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            // ダウンロードが返すのはフォルダ。ルータは拡張子で振り分ける（.gguf→llama.cpp／フォルダ→ONNX）
            // ため、ここでフォルダ内の実パス（GGUF なら .gguf ファイル）へ解決してから ModelPath に入れる。
            // フォルダのまま入れると GGUF が ONNX 行きになり「genai_config.json が見つかりません」になる。
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            var resolved = _modelCatalog.ResolvePath(name);
            ModelPath = string.IsNullOrEmpty(resolved) ? dir : resolved;   // OnModelPathChanged → Persist
            if (!string.IsNullOrEmpty(name))
            {
                if (!AvailableModels.Contains(name)) AvailableModels.Add(name);
                RefreshModelChoices();
                Model = name;
            }
            Status = $"モデルのダウンロードが完了しました: {dir}";
        }
        catch (OperationCanceledException)
        {
            Status = "ダウンロードを中止しました。";
        }
        catch (Exception ex)
        {
            Status = $"ダウンロードに失敗しました: {ex.Message}";
        }
        finally
        {
            if (_downloadCts == cts)
            {
                IsDownloading = false;
                _downloadCts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>危険コマンドのブロックリストを既定値に戻す（確認のうえ即時反映）。</summary>
    [RelayCommand]
    private void ResetBlockedCommands()
    {
        if (!Confirm("危険コマンドのブロックリストを既定値に戻します。現在の内容は失われます。よろしいですか？"))
            return;
        ApplyCommandResult(_blockedCommands.Reset());
    }

    /// <summary>破壊的な操作の前にユーザーへ確認する。アプリ未起動（テスト等）では true を返す。</summary>
    private static bool Confirm(string message)
    {
        if (System.Windows.Application.Current is null) return true;
        return System.Windows.MessageBox.Show(
            message, "Loomo",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>危険コマンドのブロックリストを中央のエディタペインで開く。保存（:w）で settings.json へ即時反映。</summary>
    [RelayCommand]
    private async Task EditBlockedCommandsAsync()
    {
        ApplyCommandResult(await _blockedCommands.OpenEditorAsync(ApplyCommandResult));
    }

    /// <summary>エディタ保存コールバックから呼ぶ共通処理：永続化して結果を表示。</summary>
    private void ApplyCommandResult(SettingsCommandResult result)
    {
        Status = result.Message;
        if (result.Success) Saved?.Invoke();
    }
}
