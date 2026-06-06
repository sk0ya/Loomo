using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>外観（カラーテーマ・アクセントカラー）の設定パネルの ViewModel。
/// テーマ／アクセントとも「色をクリックして選ぶ」スウォッチUIで、選択は共有の <see cref="AiSettings"/>（Singleton）へ
/// 書き戻し、<see cref="ThemeManager"/> で即時適用し <c>settings.json</c> へ永続化する（プレビューと保存を兼ねる）。</summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly ThemeManager _themeManager;

    /// <summary>テーマのスウォッチ（配色プレビュー）。<see cref="AppTheme"/> に値を足すだけで増やせる。</summary>
    public IReadOnlyList<ThemeSwatch> Themes { get; }

    /// <summary>アクセントカラーのスウォッチ。先頭は「テーマ既定」（Hex 空）。</summary>
    public IReadOnlyList<AccentSwatch> Accents { get; }

    /// <summary>アクセントカラーの上書き（"#RRGGBB"）。空ならテーマ既定。</summary>
    [ObservableProperty] private string _accentColor = "";

    [ObservableProperty] private string _status = "";

    /// <summary>エディタの配色テーマの選択肢。</summary>
    public IReadOnlyList<string> EditorThemes { get; } =
        new[] { "Dracula", "Dark", "Nord", "TokyoNight", "OneDark" };

    /// <summary>Markdownプレビューの配色テーマの選択肢。</summary>
    public IReadOnlyList<string> PreviewThemes { get; } =
        new[] { "Dracula", "Dark", "Light", "GitHub" };

    /// <summary>ターミナルの配色テーマ（背景/文字色）の選択肢。</summary>
    public IReadOnlyList<string> TerminalThemes { get; } =
        new[] { "Dark", "Light", "Dracula", "Nord", "SolarizedDark" };

    [ObservableProperty] private string _selectedEditorTheme = "Dracula";
    [ObservableProperty] private string _editorFontFamily = "";
    [ObservableProperty] private string _editorFontSize = "";
    [ObservableProperty] private string _selectedPreviewTheme = "Dracula";
    [ObservableProperty] private string _selectedTerminalTheme = "Dark";
    [ObservableProperty] private string _terminalFontFamily = "";
    [ObservableProperty] private string _terminalFontSize = "";

    /// <summary>エディタ／プレビュー／ターミナルの配色・フォント設定が変わったときに発火する。
    /// ホスト（ShellWindow）が購読し、開いているタブやプレビューへ即時反映する。</summary>
    public event Action? AppearanceChanged;

    public AppearanceViewModel(AiSettings settings, AiSettingsStore store, ThemeManager themeManager)
    {
        _settings = settings;
        _store = store;
        _themeManager = themeManager;

        // スウォッチの色は各パレットの代表色（Bg / Accent / Fg）を固定で持つ（適用中テーマとは独立に表示）
        Themes = new[]
        {
            new ThemeSwatch(AppTheme.Dark,          "ダーク",         "#FF1E1E1E", "#FF0E639C", "#FFD4D4D4"),
            new ThemeSwatch(AppTheme.Light,         "ライト",         "#FFFFFFFF", "#FF005FB8", "#FF1F1F1F"),
            new ThemeSwatch(AppTheme.SolarizedDark, "Solarized",      "#FF002B36", "#FF268BD2", "#FF93A1A1"),
            new ThemeSwatch(AppTheme.Nord,          "Nord",           "#FF2E3440", "#FF5E81AC", "#FFD8DEE9"),
            new ThemeSwatch(AppTheme.HighContrast,  "高コントラスト", "#FF000000", "#FF1AEBFF", "#FFFFFFFF"),
        };

        Accents = new[]
        {
            new AccentSwatch("テーマ既定", ""),
            new AccentSwatch("ブルー",     "#FF0E639C"),
            new AccentSwatch("シアン",     "#FF1B9E8F"),
            new AccentSwatch("グリーン",   "#FF2EA043"),
            new AccentSwatch("パープル",   "#FF8957E5"),
            new AccentSwatch("マゼンタ",   "#FFBF4080"),
            new AccentSwatch("レッド",     "#FFD13438"),
            new AccentSwatch("オレンジ",   "#FFD9730D"),
        };

        _accentColor = settings.AccentColor ?? "";
        SetThemeSelection(settings.Theme);
        SetAccentSelection(_accentColor);

        var ap = settings.Appearance;
        _selectedEditorTheme = NormalizeChoice(ap.EditorTheme, EditorThemes, "Dracula");
        _editorFontFamily = ap.EditorFontFamily ?? "";
        _editorFontSize = ap.EditorFontSize > 0 ? ap.EditorFontSize.ToString("0.#") : "";
        _selectedPreviewTheme = NormalizeChoice(ap.MarkdownPreviewTheme, PreviewThemes, "Dracula");
        _selectedTerminalTheme = NormalizeChoice(ap.TerminalTheme, TerminalThemes, "Dark");
        _terminalFontFamily = ap.TerminalFontFamily ?? "";
        _terminalFontSize = ap.TerminalFontSize > 0 ? ap.TerminalFontSize.ToString("0.#") : "";
    }

    /// <summary>保存値が選択肢に無い場合は既定へ寄せる（大文字小文字は無視）。</summary>
    private static string NormalizeChoice(string? value, IReadOnlyList<string> choices, string fallback)
    {
        foreach (var c in choices)
            if (string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) return c;
        return fallback;
    }

    /// <summary>テーマのスウォッチをクリック：即時適用＆永続化（アクセント上書きは維持される）。</summary>
    [RelayCommand]
    private void SelectTheme(AppTheme theme)
    {
        _settings.Theme = theme;
        _themeManager.ApplyTheme(theme);
        SetThemeSelection(theme);
        Persist("テーマを変更しました");
    }

    /// <summary>アクセントのスウォッチをクリック：<see cref="AccentColor"/> 経由で適用する（空=テーマ既定）。</summary>
    [RelayCommand]
    private void SelectAccent(string? hex) => AccentColor = hex ?? "";

    /// <summary>アクセント指定が変わったら検証して即時適用＆永続化。空ならテーマ既定へ戻す。</summary>
    partial void OnAccentColorChanged(string value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            _settings.AccentColor = null;
            _themeManager.ApplyAccentColor(null);
            SetAccentSelection("");
            Persist("アクセントをテーマ既定に戻しました");
            return;
        }
        if (!ThemeManager.IsValidColor(trimmed))
        {
            Status = "無効なカラー指定です（例: #1E90FF）";
            // 入力欄を直前に適用済みの値へ戻し、表示と実際の配色のずれを残さない。
            // 背面フィールドを直接書き換えて OnAccentColorChanged の再入を避ける（エラー表示を保つ）。
            _accentColor = _settings.AccentColor ?? "";
            OnPropertyChanged(nameof(AccentColor));
            return;
        }
        _settings.AccentColor = trimmed;
        _themeManager.ApplyAccentColor(trimmed);
        SetAccentSelection(trimmed);
        Persist("アクセントカラーを変更しました");
    }

    partial void OnSelectedEditorThemeChanged(string value)
    {
        _settings.Appearance.EditorTheme = value;
        PersistAppearance("エディタのテーマを変更しました");
    }

    partial void OnEditorFontFamilyChanged(string value)
    {
        _settings.Appearance.EditorFontFamily = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        PersistAppearance("エディタのフォントを変更しました");
    }

    partial void OnEditorFontSizeChanged(string value) =>
        ApplyFontSize(value, v => _settings.Appearance.EditorFontSize = v,
            () => _settings.Appearance.EditorFontSize, () => _editorFontSize,
            v => _editorFontSize = v, nameof(EditorFontSize), "エディタのフォントサイズを変更しました");

    partial void OnSelectedPreviewThemeChanged(string value)
    {
        _settings.Appearance.MarkdownPreviewTheme = value;
        PersistAppearance("プレビューのテーマを変更しました");
    }

    partial void OnSelectedTerminalThemeChanged(string value)
    {
        _settings.Appearance.TerminalTheme = value;
        PersistAppearance("ターミナルのテーマを変更しました");
    }

    partial void OnTerminalFontFamilyChanged(string value)
    {
        _settings.Appearance.TerminalFontFamily = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        PersistAppearance("ターミナルのフォントを変更しました");
    }

    partial void OnTerminalFontSizeChanged(string value) =>
        ApplyFontSize(value, v => _settings.Appearance.TerminalFontSize = v,
            () => _settings.Appearance.TerminalFontSize, () => _terminalFontSize,
            v => _terminalFontSize = v, nameof(TerminalFontSize), "ターミナルのフォントサイズを変更しました");

    /// <summary>フォントサイズ入力を検証して設定へ反映する。空なら既定(0)へ、数値なら適用、
    /// 不正値は直前値へ戻す（<see cref="OnAccentColorChanged"/> と同じ戻し方）。</summary>
    private void ApplyFontSize(
        string value, Action<double> set, Func<double> get,
        Func<string> getField, Action<string> setField, string propertyName, string message)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            set(0);
            PersistAppearance("フォントサイズを既定に戻しました");
            return;
        }
        if (!double.TryParse(trimmed, out var size) || size < 4 || size > 96)
        {
            Status = "無効なフォントサイズです（4〜96）";
            setField(get() > 0 ? get().ToString("0.#") : "");
            OnPropertyChanged(propertyName);
            return;
        }
        set(size);
        PersistAppearance(message);
    }

    /// <summary>外観（エディタ/プレビュー/ターミナル）の変更を保存し、ホストへ即時反映を促す。</summary>
    private void PersistAppearance(string message)
    {
        Persist(message);
        AppearanceChanged?.Invoke();
    }

    private void SetThemeSelection(AppTheme theme)
    {
        foreach (var s in Themes) s.IsSelected = s.Theme == theme;
    }

    private void SetAccentSelection(string hex)
    {
        foreach (var a in Accents)
            a.IsSelected = string.Equals(a.Hex, hex, StringComparison.OrdinalIgnoreCase);
    }

    private void Persist(string message)
    {
        try
        {
            _store.Save(_settings);
            Status = message;
        }
        catch (Exception ex)
        {
            Status = $"保存に失敗しました: {ex.Message}";
        }
    }

    /// <summary>テーマのスウォッチ1つ。配色プレビュー用の代表色と選択状態を持つ。</summary>
    public sealed partial class ThemeSwatch : ObservableObject
    {
        public AppTheme Theme { get; }
        public string Name { get; }
        public string Bg { get; }
        public string Accent { get; }
        public string Fg { get; }
        [ObservableProperty] private bool _isSelected;

        public ThemeSwatch(AppTheme theme, string name, string bg, string accent, string fg)
        {
            Theme = theme; Name = name; Bg = bg; Accent = accent; Fg = fg;
        }
    }

    /// <summary>アクセントのスウォッチ1つ。Hex が空文字なら「テーマ既定」。</summary>
    public sealed partial class AccentSwatch : ObservableObject
    {
        public string Name { get; }
        public string Hex { get; }
        [ObservableProperty] private bool _isSelected;

        public AccentSwatch(string name, string hex) { Name = name; Hex = hex; }
    }
}
