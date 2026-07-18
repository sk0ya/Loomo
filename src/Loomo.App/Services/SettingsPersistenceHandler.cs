using sk0ya.Loomo.Ai;

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
    public bool AutoClosePairs { get; init; }
    public int TabWidth { get; init; }
    public bool UseSpacesForTab { get; init; }
    public string ImagePasteDirectory { get; init; } = "";
    public string ImagePasteFileName { get; init; } = "";
    public string ImagePasteAltText { get; init; } = "";
    public bool AutoApprove { get; init; }
    public bool RestrictToWorkspaceRoot { get; init; }
}

/// <summary>設定フォームと永続化モデルの相互変換および保存を担当する。</summary>
public sealed class SettingsPersistenceHandler
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;

    public SettingsPersistenceHandler(AiSettings settings, AiSettingsStore store)
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
        AutoClosePairs = _settings.Editor.AutoClosePairs,
        TabWidth = _settings.Editor.TabWidth,
        UseSpacesForTab = _settings.Editor.UseSpacesForTab,
        ImagePasteDirectory = _settings.Editor.ImagePasteDirectory,
        ImagePasteFileName = _settings.Editor.ImagePasteFileName,
        ImagePasteAltText = _settings.Editor.ImagePasteAltText,
        AutoApprove = _settings.Safety.AutoApprove,
        RestrictToWorkspaceRoot = _settings.Safety.RestrictToWorkspaceRoot,
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
        _settings.Editor.AutoClosePairs = form.AutoClosePairs;
        _settings.Editor.TabWidth = form.TabWidth > 0 ? form.TabWidth : 2;
        _settings.Editor.UseSpacesForTab = form.UseSpacesForTab;
        _settings.Editor.ImagePasteDirectory = form.ImagePasteDirectory.Trim();
        _settings.Editor.ImagePasteFileName = form.ImagePasteFileName.Trim();
        _settings.Editor.ImagePasteAltText = form.ImagePasteAltText.Trim();
        _settings.Safety.AutoApprove = form.AutoApprove;
        _settings.Safety.RestrictToWorkspaceRoot = form.RestrictToWorkspaceRoot;
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
