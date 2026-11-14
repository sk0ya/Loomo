using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定パネル（エディタ等）の ViewModel。
/// 編集内容は共有の <see cref="LoomoSettings"/>（Singleton）へ書き戻し、変更のたびにファイルへ永続化する。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsPersistenceHandler _persistence;
    // 初期ロード中の代入で自動保存（Persist）が走らないようにするためのガード。
    private bool _suppressPersist = true;

    public event Action? Saved;

    [ObservableProperty] private bool _vimEnabled;
    [ObservableProperty] private bool _highlightWhitespace;
    [ObservableProperty] private bool _showLineNumbers;
    [ObservableProperty] private bool _relativeLineNumbers;
    [ObservableProperty] private bool _highlightCurrentLine;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _showMinimap;
    [ObservableProperty] private bool _showIndentGuides;
    [ObservableProperty] private bool _collapseUsingsOnOpen;
    [ObservableProperty] private bool _cleanCSharpOnSave;
    [ObservableProperty] private bool _cleanCSharpOnSaveReformatOnly;
    [ObservableProperty] private bool _cleanCSharpOnSaveChangedLinesOnly;

    /// <summary>全言語の保存時整形（整形カテゴリ）。</summary>
    [ObservableProperty] private bool _formatOnSave;

    /// <summary>保存時整形から外す拡張子（空白区切りの入力）。</summary>
    [ObservableProperty] private string _formatOnSaveExcludedExtensions = "";

    /// <summary>保存時に .editorconfig の空白規則を適用するか（整形カテゴリ）。</summary>
    [ObservableProperty] private bool _applyEditorConfigOnSave;
    [ObservableProperty] private bool _autoClosePairs;
    [ObservableProperty] private bool _linkedEditing;
    [ObservableProperty] private bool _showInlayHints;
    [ObservableProperty] private bool _showCaretLineBlame;
    [ObservableProperty] private bool _showDebugInlineValues = true;
    /// <summary>裏で git fetch する（§24.92）。</summary>
    [ObservableProperty] private bool _gitAutoFetch = true;
    /// <summary>取り消しの履歴を残す（§24.96）。</summary>
    [ObservableProperty] private bool _persistUndoHistory = true;
    [ObservableProperty] private int _tabWidth;
    [ObservableProperty] private bool _useSpacesForTab;
    [ObservableProperty] private string _imagePasteDirectory = "";
    [ObservableProperty] private string _imagePasteFileName = "";
    [ObservableProperty] private string _imagePasteAltText = "";
    [ObservableProperty] private string _status = "";

    /// <summary>入力の先読み（エディタ内蔵の予測）を使うか。</summary>
    [ObservableProperty] private bool _inlineSuggest = true;

    public SettingsViewModel(SettingsPersistenceHandler persistence)
    {
        _persistence = persistence;
        LoadLocalFields();
    }

    private void LoadLocalFields()
    {
        // 初期ロード中の代入で自動保存が走らないよう抑止する。
        _suppressPersist = true;
        var form = _persistence.Load();
        VimEnabled = form.VimEnabled;
        HighlightWhitespace = form.HighlightWhitespace; ShowLineNumbers = form.ShowLineNumbers;
        RelativeLineNumbers = form.RelativeLineNumbers; HighlightCurrentLine = form.HighlightCurrentLine;
        WordWrap = form.WordWrap; ShowMinimap = form.ShowMinimap; ShowIndentGuides = form.ShowIndentGuides;
        CollapseUsingsOnOpen = form.CollapseUsingsOnOpen;
        CleanCSharpOnSave = form.CleanCSharpOnSave;
        CleanCSharpOnSaveReformatOnly = form.CleanCSharpOnSaveReformatOnly;
        CleanCSharpOnSaveChangedLinesOnly = form.CleanCSharpOnSaveChangedLinesOnly;
        FormatOnSave = form.FormatOnSave;
        FormatOnSaveExcludedExtensions = form.FormatOnSaveExcludedExtensions;
        ApplyEditorConfigOnSave = form.ApplyEditorConfigOnSave;
        AutoClosePairs = form.AutoClosePairs; ShowInlayHints = form.ShowInlayHints;
        ShowCaretLineBlame = form.ShowCaretLineBlame;
        ShowDebugInlineValues = form.ShowDebugInlineValues;
        GitAutoFetch = form.GitAutoFetch;
        PersistUndoHistory = form.PersistUndoHistory;
        LinkedEditing = form.LinkedEditing;
        TabWidth = form.TabWidth; UseSpacesForTab = form.UseSpacesForTab;
        ImagePasteDirectory = form.ImagePasteDirectory; ImagePasteFileName = form.ImagePasteFileName;
        ImagePasteAltText = form.ImagePasteAltText;
        InlineSuggest = form.InlineSuggest;
        _suppressPersist = false;
    }

    // 「保存」ボタンは廃止。各項目の変更はその場で共有 LoomoSettings へ反映し、ファイルへ即永続化する。
    partial void OnVimEnabledChanged(bool value) => Persist();
    partial void OnHighlightWhitespaceChanged(bool value) => Persist();
    partial void OnShowLineNumbersChanged(bool value) => Persist();
    partial void OnRelativeLineNumbersChanged(bool value) => Persist();
    partial void OnHighlightCurrentLineChanged(bool value) => Persist();
    partial void OnWordWrapChanged(bool value) => Persist();
    partial void OnShowMinimapChanged(bool value) => Persist();
    partial void OnShowIndentGuidesChanged(bool value) => Persist();
    partial void OnCollapseUsingsOnOpenChanged(bool value) => Persist();
    partial void OnCleanCSharpOnSaveChanged(bool value) => Persist();
    partial void OnCleanCSharpOnSaveReformatOnlyChanged(bool value) => Persist();
    partial void OnCleanCSharpOnSaveChangedLinesOnlyChanged(bool value) => Persist();
    partial void OnFormatOnSaveChanged(bool value) => Persist();
    partial void OnFormatOnSaveExcludedExtensionsChanged(string value) => Persist();
    partial void OnApplyEditorConfigOnSaveChanged(bool value) => Persist();
    partial void OnAutoClosePairsChanged(bool value) => Persist();
    partial void OnLinkedEditingChanged(bool value) => Persist();
    partial void OnShowInlayHintsChanged(bool value) => Persist();
    partial void OnShowCaretLineBlameChanged(bool value) => Persist();
    partial void OnShowDebugInlineValuesChanged(bool value) => Persist();
    partial void OnGitAutoFetchChanged(bool value) => Persist();
    partial void OnPersistUndoHistoryChanged(bool value) => Persist();
    partial void OnTabWidthChanged(int value) => Persist();
    partial void OnUseSpacesForTabChanged(bool value) => Persist();
    partial void OnImagePasteDirectoryChanged(string value) => Persist();
    partial void OnImagePasteFileNameChanged(string value) => Persist();
    partial void OnImagePasteAltTextChanged(string value) => Persist();
    partial void OnInlineSuggestChanged(bool value) => Persist();

    /// <summary>変更を共有 <see cref="LoomoSettings"/> へ即時反映し、ファイルへ永続化する。
    /// 「保存」ボタンを廃した代わりに、各項目の変更時に自動で呼ばれる（初期ロード中は抑止）。</summary>
    private void Persist()
    {
        if (_suppressPersist) return;

        var result = _persistence.Save(new SettingsFormState
        {
            VimEnabled = VimEnabled, HighlightWhitespace = HighlightWhitespace, ShowLineNumbers = ShowLineNumbers,
            RelativeLineNumbers = RelativeLineNumbers, HighlightCurrentLine = HighlightCurrentLine,
            WordWrap = WordWrap, ShowMinimap = ShowMinimap, ShowIndentGuides = ShowIndentGuides,
            CollapseUsingsOnOpen = CollapseUsingsOnOpen,
            CleanCSharpOnSave = CleanCSharpOnSave,
            CleanCSharpOnSaveReformatOnly = CleanCSharpOnSaveReformatOnly,
            CleanCSharpOnSaveChangedLinesOnly = CleanCSharpOnSaveChangedLinesOnly,
            FormatOnSave = FormatOnSave,
            FormatOnSaveExcludedExtensions = FormatOnSaveExcludedExtensions,
            ApplyEditorConfigOnSave = ApplyEditorConfigOnSave,
            AutoClosePairs = AutoClosePairs, ShowInlayHints = ShowInlayHints,
            ShowCaretLineBlame = ShowCaretLineBlame,
            ShowDebugInlineValues = ShowDebugInlineValues,
            GitAutoFetch = GitAutoFetch,
            PersistUndoHistory = PersistUndoHistory,
            LinkedEditing = LinkedEditing,
            TabWidth = TabWidth, UseSpacesForTab = UseSpacesForTab,
            ImagePasteDirectory = ImagePasteDirectory, ImagePasteFileName = ImagePasteFileName,
            ImagePasteAltText = ImagePasteAltText,
            InlineSuggest = InlineSuggest,
        });
        ApplyCommandResult(result);
    }

    /// <summary>永続化の結果を表示する。</summary>
    private void ApplyCommandResult(SettingsCommandResult result)
    {
        Status = result.Message;
        if (result.Success) Saved?.Invoke();
    }
}
