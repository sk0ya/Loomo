using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定パネル（Ollama モデル等）の ViewModel。
/// 編集内容は共有の <see cref="AiSettings"/>（Singleton）へ書き戻し、保存時にファイルへ永続化する。
/// 危険コマンド一覧などの長文項目は、狭いサイドバーではなく中央のエディタ領域で
/// 編集する（<see cref="IEditorService.OpenDocumentAsync"/>。保存=:w 時にコールバックで即時反映）。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly IEditorService _editor;
    private readonly ModelCatalogService _modelCatalog;
    private readonly ModelDownloadService _modelDownload;
    private CancellationTokenSource? _fetchModelsCts;
    private CancellationTokenSource? _downloadCts;
    // 初期ロード中の代入で自動保存（Persist）が走らないようにするためのガード。
    private bool _suppressPersist = true;

    /// <summary>設定が保存されたときに通知（AIバーのプロバイダ表示更新などに使う）。</summary>
    public event Action? Saved;

    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private int _maxTokens;
    [ObservableProperty] private bool _vimEnabled;
    [ObservableProperty] private string _status = "";

    /// <summary>モデルをダウンロード中か。</summary>
    [ObservableProperty] private bool _isDownloading;

    /// <summary>ダウンロード進捗（0–100、不明時は不定表示用に -1）。</summary>
    [ObservableProperty] private double _downloadProgress;

    // --- 安全設計（設計書 §10） ---
    [ObservableProperty] private bool _autoApprove;
    [ObservableProperty] private bool _restrictToWorkspaceRoot;

    /// <summary>エンドポイントから取得した利用可能モデルの一覧（選択肢）。</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>モデル一覧を取得中か。</summary>
    [ObservableProperty] private bool _isFetchingModels;

    /// <summary>モデル選択ドロップダウンを開いているか（XAML の IsDropDownOpen と双方向バインド）。</summary>
    [ObservableProperty] private bool _modelDropDownOpen;

    public SettingsViewModel(AiSettings settings, AiSettingsStore store,
        IEditorService editor, ModelCatalogService modelCatalog, ModelDownloadService modelDownload)
    {
        _settings = settings;
        _store = store;
        _editor = editor;
        _modelCatalog = modelCatalog;
        _modelDownload = modelDownload;
        _autoApprove = settings.Safety.AutoApprove;
        _restrictToWorkspaceRoot = settings.Safety.RestrictToWorkspaceRoot;
        LoadLocalFields();
    }

    public void SyncProvider(AiProvider provider) { }

    private void LoadLocalFields()
    {
        // 初期ロード中の代入で自動保存が走らないよう抑止する。
        _suppressPersist = true;
        var cfg = _settings.Local;
        Model = cfg.Model;
        ModelPath = cfg.ModelPath;
        MaxTokens = cfg.MaxTokens;
        VimEnabled = _settings.Vim.Enabled;
        _suppressPersist = false;
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
        Persist();
    }
    partial void OnModelPathChanged(string value) => Persist();
    partial void OnMaxTokensChanged(int value) => Persist();
    partial void OnVimEnabledChanged(bool value) => Persist();
    partial void OnAutoApproveChanged(bool value) => Persist();
    partial void OnRestrictToWorkspaceRootChanged(bool value) => Persist();

    private void CommitLocalFields()
    {
        var cfg = _settings.Local;
        // 手入力途中の空値ではモデルを上書きしない（実行中モデルを消さないため）。
        var name = Model.Trim();
        if (name.Length > 0) cfg.Model = name;
        cfg.ModelPath = ModelPath.Trim();
        cfg.ApiKey = null;
        cfg.MaxTokens = MaxTokens > 0 ? MaxTokens : 4096;
    }

    /// <summary>変更を共有 <see cref="AiSettings"/> へ即時反映し、ファイルへ永続化する。
    /// 「保存」ボタンを廃した代わりに、各項目の変更時に自動で呼ばれる（初期ロード中は抑止）。
    /// 危険コマンド一覧はエディタの保存（:w）時に別途反映するため、ここでは扱わない。</summary>
    private void Persist()
    {
        if (_suppressPersist) return;

        CommitLocalFields();
        _settings.Provider = AiProvider.Local;
        _settings.Vim.Enabled = VimEnabled;
        _settings.Safety.AutoApprove = AutoApprove;
        _settings.Safety.RestrictToWorkspaceRoot = RestrictToWorkspaceRoot;

        try
        {
            _store.Save(_settings);
            Status = "設定を反映しました（自動保存済み）";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"保存に失敗しました: {ex.Message}";
        }
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

    /// <summary>ローカルに配置済みの ONNX モデルフォルダを列挙し、選択肢に反映する。
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

            if (AvailableModels.Count == 0)
            {
                Status = "ローカルにモデルが見つかりませんでした。「ダウンロード」または「別の場所から追加」で用意してください。";
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

    /// <summary>ONNX モデルフォルダを手動で選ぶ（任意の場所に置いたモデル用）。
    /// 選んだフォルダを ModelPath に設定し、モデル名（プロファイル解決用）にフォルダ名を流用する。</summary>
    [RelayCommand]
    private void BrowseModel()
    {
        var initial = !string.IsNullOrWhiteSpace(ModelPath) && Directory.Exists(ModelPath)
            ? ModelPath
            : ModelDownloadService.DefaultModelsRoot;
        var dialog = new OpenFolderDialog
        {
            Title = "ONNX モデルフォルダを選択（genai_config.json を含むフォルダ）",
            InitialDirectory = Directory.Exists(initial) ? initial : null,
        };
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (!File.Exists(Path.Combine(folder, "genai_config.json")))
        {
            Status = "選択したフォルダに genai_config.json がありません（ONNX モデルフォルダではありません）。";
            return;
        }

        ModelPath = folder;                          // OnModelPathChanged → Persist
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(name) && !AvailableModels.Contains(name))
            AvailableModels.Add(name);
        if (!string.IsNullOrEmpty(name))
            Model = name;
        Status = $"モデルフォルダを設定しました: {folder}";
    }

    /// <summary>phi4-mini（ONNX・CPU int4）を Hugging Face からダウンロードして設定する。</summary>
    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (IsDownloading) return;
        _downloadCts?.Cancel();
        var cts = _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        try
        {
            Status = "モデルをダウンロードしています…";
            var progress = new Progress<ModelDownloadService.Progress>(p =>
            {
                DownloadProgress = p.TotalBytes > 0 ? p.DownloadedBytes * 100.0 / p.TotalBytes : -1;
                var pct = p.TotalBytes > 0 ? $"{DownloadProgress:0}%" : $"{p.DownloadedBytes / (1024 * 1024)}MB";
                Status = $"ダウンロード中 ({p.FileIndex}/{p.FileCount}) {p.CurrentFile} — {pct}";
            });

            var dir = await _modelDownload.DownloadAsync(progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            ModelPath = dir;                          // OnModelPathChanged → Persist
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name))
            {
                if (!AvailableModels.Contains(name)) AvailableModels.Add(name);
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
        _settings.Safety.BlockedCommandPatterns =
            new List<string>(SafetySettings.DefaultBlockedPatterns);
        PersistAndNotify("危険コマンドのブロックリストを既定値に戻しました");
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
        const string header =
            "# ブロックする危険コマンド（run_powershell の照合に使用）\n" +
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
}
