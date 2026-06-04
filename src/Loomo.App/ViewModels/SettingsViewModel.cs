using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private CancellationTokenSource? _fetchModelsCts;

    /// <summary>設定が保存されたときに通知（AIバーのプロバイダ表示更新などに使う）。</summary>
    public event Action? Saved;

    [ObservableProperty] private string _model = "";
    [ObservableProperty] private int _maxTokens;
    [ObservableProperty] private bool _thinking;
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

    public bool CanFetchModels => true;

    public SettingsViewModel(AiSettings settings, AiSettingsStore store,
        IEditorService editor, ModelCatalogService modelCatalog)
    {
        _settings = settings;
        _store = store;
        _editor = editor;
        _modelCatalog = modelCatalog;
        _autoApprove = settings.Safety.AutoApprove;
        _restrictToWorkspaceRoot = settings.Safety.RestrictToWorkspaceRoot;
        LoadLocalFields();
    }

    public void SyncProvider(AiProvider provider) { }

    private void LoadLocalFields()
    {
        var cfg = _settings.Local;
        Model = cfg.Model;
        MaxTokens = cfg.MaxTokens;
        Thinking = cfg.Thinking;
    }

    private void CommitLocalFields()
    {
        var cfg = _settings.Local;
        cfg.Model = Model.Trim();
        cfg.ApiKey = null;
        cfg.MaxTokens = MaxTokens > 0 ? MaxTokens : 4096;
        cfg.Thinking = Thinking;
    }

    [RelayCommand]
    private void Save()
    {
        CommitLocalFields();
        _settings.Provider = AiProvider.Local;

        // 安全設計を書き戻す（同一インスタンスなので即時反映される）
        _settings.Safety.AutoApprove = AutoApprove;
        _settings.Safety.RestrictToWorkspaceRoot = RestrictToWorkspaceRoot;
        // 危険コマンド一覧はエディタでの保存（:w）時に即時反映するため、ここでは扱わない。

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

    /// <summary>設定パネルを開いたときに呼ぶ。まだ候補が無ければ Ollama から自動取得する
    /// （取得済み／取得中なら何もしない）。手動更新は「再取得」ボタンで行える。</summary>
    public void EnsureModelsLoaded()
    {
        if (IsFetchingModels || AvailableModels.Count > 0) return;
        _ = FetchModelsCoreAsync(autoOpen: false);
    }

    /// <summary>手動の「再取得」ボタン。取得後に候補ドロップダウンを開いて見せる。</summary>
    [RelayCommand]
    private Task FetchModelsAsync() => FetchModelsCoreAsync(autoOpen: true);

    /// <summary>固定のローカル Ollama エンドポイントから利用可能なモデル一覧を取得し、選択肢に反映する。
    /// <paramref name="autoOpen"/> が true なら取得成功後にドロップダウンを自動で開く。</summary>
    private async Task FetchModelsCoreAsync(bool autoOpen)
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
            var models = await _modelCatalog.FetchAsync(
                provider,
                ct: cts.Token);

            // 取得中に中止／プロバイダ変更があれば結果は破棄する。
            if (cts.IsCancellationRequested) return;

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
            "# ブロックする危険コマンド（pwsh の照合に使用）\n" +
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
