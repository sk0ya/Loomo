using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Services.Formatting;

namespace sk0ya.Loomo.App.Services;

public sealed record SettingsFormState
{
    public string Model { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public int MaxTokens { get; init; }
    public bool WarmupEnabled { get; init; }
    public bool VimEnabled { get; init; }
    public bool HighlightWhitespace { get; init; }
    public bool ShowLineNumbers { get; init; }
    public bool RelativeLineNumbers { get; init; }
    public bool HighlightCurrentLine { get; init; }
    public bool WordWrap { get; init; }
    public bool ShowMinimap { get; init; }
    public bool ShowIndentGuides { get; init; }
    public bool CollapseUsingsOnOpen { get; init; }
    public bool CleanCSharpOnSave { get; init; }

    /// <summary>全言語の保存時整形（:Format と同じ経路）。</summary>
    public bool FormatOnSave { get; init; }

    /// <summary>保存時整形から外す拡張子（設定画面の入力そのまま。空白・カンマ区切り）。</summary>
    public string FormatOnSaveExcludedExtensions { get; init; } = "";

    /// <summary>保存時に .editorconfig の空白規則（行末空白・末尾改行・改行コード）を適用するか。</summary>
    public bool ApplyEditorConfigOnSave { get; init; }
    public bool AutoClosePairs { get; init; }
    public bool ShowInlayHints { get; init; }
    public bool ShowDebugInlineValues { get; init; }

    /// <summary>入力の先読みそのものを使うか（内蔵の予測とローカル LLM の両方をまとめて切る）。</summary>
    public bool InlineSuggest { get; init; }
    public int TabWidth { get; init; }
    public bool UseSpacesForTab { get; init; }
    public string ImagePasteDirectory { get; init; } = "";
    public string ImagePasteFileName { get; init; } = "";
    public string ImagePasteAltText { get; init; } = "";
    public bool AutoApprove { get; init; }
    public bool RestrictToWorkspaceRoot { get; init; }

    /// <summary>入力の先読みをローカル LLM でも行うか（エディタ内蔵の予測はこれと関係なく常に動く）。</summary>
    public bool InlineCompletionEnabled { get; init; }

    /// <summary>先読み用 FIM モデル（<c>.gguf</c>）のパス。</summary>
    public string InlineCompletionModelPath { get; init; } = "";
}

/// <summary>設定フォームと永続化モデルの相互変換および保存を担当する。</summary>
public sealed class SettingsPersistenceHandler
{
    private readonly LoomoSettings _settings;
    private readonly SettingsStore _store;

    public SettingsPersistenceHandler(LoomoSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    public SettingsFormState Load() => new()
    {
        Model = _settings.Local.Model,
        ModelPath = _settings.Local.ModelPath,
        MaxTokens = _settings.Local.MaxTokens,
        WarmupEnabled = _settings.WarmupEnabled,
        VimEnabled = _settings.Vim.Enabled,
        HighlightWhitespace = _settings.Editor.HighlightWhitespace,
        ShowLineNumbers = _settings.Editor.ShowLineNumbers,
        RelativeLineNumbers = _settings.Editor.RelativeLineNumbers,
        HighlightCurrentLine = _settings.Editor.HighlightCurrentLine,
        WordWrap = _settings.Editor.WordWrap,
        ShowMinimap = _settings.Editor.ShowMinimap,
        ShowIndentGuides = _settings.Editor.ShowIndentGuides,
        CollapseUsingsOnOpen = _settings.Editor.CollapseUsingsOnOpen,
        CleanCSharpOnSave = _settings.Editor.CleanCSharpOnSave,
        FormatOnSave = _settings.Editor.FormatOnSave,
        FormatOnSaveExcludedExtensions = FormatOnSavePolicy.FormatExtensions(
            _settings.Editor.FormatOnSaveExcludedExtensions),
        ApplyEditorConfigOnSave = _settings.Editor.ApplyEditorConfigOnSave,
        AutoClosePairs = _settings.Editor.AutoClosePairs,
        ShowInlayHints = _settings.Editor.ShowInlayHints,
        ShowDebugInlineValues = _settings.Editor.ShowDebugInlineValues,
        InlineSuggest = _settings.Editor.InlineSuggest,
        TabWidth = _settings.Editor.TabWidth,
        UseSpacesForTab = _settings.Editor.UseSpacesForTab,
        ImagePasteDirectory = _settings.Editor.ImagePasteDirectory,
        ImagePasteFileName = _settings.Editor.ImagePasteFileName,
        ImagePasteAltText = _settings.Editor.ImagePasteAltText,
        AutoApprove = _settings.Safety.AutoApprove,
        RestrictToWorkspaceRoot = _settings.Safety.RestrictToWorkspaceRoot,
        InlineCompletionEnabled = _settings.InlineCompletion.Enabled,
        InlineCompletionModelPath = _settings.InlineCompletion.ModelPath ?? "",
    };

    public SettingsCommandResult Save(SettingsFormState form)
    {
        var model = form.Model.Trim();
        if (model.Length > 0) _settings.Local.Model = model;
        _settings.Local.ModelPath = form.ModelPath.Trim();
        _settings.Local.ApiKey = null;
        _settings.Local.MaxTokens = form.MaxTokens > 0 ? form.MaxTokens : 4096;
        _settings.Provider = AiProvider.Local;
        _settings.WarmupEnabled = form.WarmupEnabled;
        _settings.Vim.Enabled = form.VimEnabled;
        _settings.Editor.HighlightWhitespace = form.HighlightWhitespace;
        _settings.Editor.ShowLineNumbers = form.ShowLineNumbers;
        _settings.Editor.RelativeLineNumbers = form.RelativeLineNumbers;
        _settings.Editor.HighlightCurrentLine = form.HighlightCurrentLine;
        _settings.Editor.WordWrap = form.WordWrap;
        _settings.Editor.ShowMinimap = form.ShowMinimap;
        _settings.Editor.ShowIndentGuides = form.ShowIndentGuides;
        _settings.Editor.CollapseUsingsOnOpen = form.CollapseUsingsOnOpen;
        _settings.Editor.CleanCSharpOnSave = form.CleanCSharpOnSave;
        _settings.Editor.FormatOnSave = form.FormatOnSave;
        _settings.Editor.FormatOnSaveExcludedExtensions =
            FormatOnSavePolicy.ParseExtensions(form.FormatOnSaveExcludedExtensions);
        _settings.Editor.ApplyEditorConfigOnSave = form.ApplyEditorConfigOnSave;
        _settings.Editor.AutoClosePairs = form.AutoClosePairs;
        _settings.Editor.ShowInlayHints = form.ShowInlayHints;
        _settings.Editor.ShowDebugInlineValues = form.ShowDebugInlineValues;
        _settings.Editor.InlineSuggest = form.InlineSuggest;
        _settings.Editor.TabWidth = form.TabWidth > 0 ? form.TabWidth : 2;
        _settings.Editor.UseSpacesForTab = form.UseSpacesForTab;
        _settings.Editor.ImagePasteDirectory = form.ImagePasteDirectory.Trim();
        _settings.Editor.ImagePasteFileName = form.ImagePasteFileName.Trim();
        _settings.Editor.ImagePasteAltText = form.ImagePasteAltText.Trim();
        _settings.Safety.AutoApprove = form.AutoApprove;
        _settings.Safety.RestrictToWorkspaceRoot = form.RestrictToWorkspaceRoot;
        var completionModel = form.InlineCompletionModelPath.Trim();
        _settings.InlineCompletion.ModelPath = completionModel.Length == 0 ? null : completionModel;
        // モデルが無いのに有効のままだと「効かない設定が入っている」状態になる。
        _settings.InlineCompletion.Enabled = form.InlineCompletionEnabled && completionModel.Length > 0;
        try
        {
            _store.Save(_settings);
            return new SettingsCommandResult(true, "設定を反映しました（自動保存済み）");
        }
        catch (Exception ex)
        {
            return new SettingsCommandResult(false, $"保存に失敗しました: {ex.Message}");
        }
    }
}
