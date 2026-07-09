using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>外観（カラーテーマ・アクセント・各ペインの配色／フォント）の設定パネルの ViewModel。
/// アプリ全体・エディタ・ターミナル・Markdownプレビューの配色、およびアクセントは、いずれも「色チップ付きの
/// コンボボックスから選ぶ」同一UIに統一する。選択は共有の <see cref="AiSettings"/>（Singleton）へ書き戻して
/// 即時適用＋<c>settings.json</c> へ永続化する。アプリ全体テーマは <see cref="ThemeManager"/> 経由、その他の
/// ペインは <see cref="AppearanceChanged"/> でホスト（ShellWindow）へ即時反映を促す。</summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly ThemeManager _themeManager;
    private readonly UiFontManager _UiFontManager;

    /// <summary>アプリ全体のカラーテーマの選択肢。<see cref="AppTheme"/> に値を足すだけで増やせる。</summary>
    public IReadOnlyList<PresetSwatch> Themes { get; }

    /// <summary>アクセントカラーの選択肢。先頭は「テーマ既定」（Hex 空）。</summary>
    public IReadOnlyList<AccentSwatch> Accents { get; }

    /// <summary>エディタの配色テーマの選択肢。チップの色はライブラリ各プリセットの背景／文字／アクセント。</summary>
    public IReadOnlyList<PresetSwatch> EditorThemes { get; }

    /// <summary>Markdownプレビューの配色テーマの選択肢。</summary>
    public IReadOnlyList<PresetSwatch> PreviewThemes { get; }

    /// <summary>ターミナルの配色テーマの選択肢（背景／文字色／カーソル色）。</summary>
    public IReadOnlyList<PresetSwatch> TerminalThemes { get; }

    /// <summary>アプリ UI 全体の基準フォントサイズ（本文の px）の選択肢。既定は 16px。</summary>
    public IReadOnlyList<FontSizeChoice> FontSizes { get; } = new[]
    {
        new FontSizeChoice(12, "12 px"),
        new FontSizeChoice(13, "13 px"),
        new FontSizeChoice(14, "14 px"),
        new FontSizeChoice(15, "15 px"),
        new FontSizeChoice(16, "16 px（既定）"),
        new FontSizeChoice(17, "17 px"),
        new FontSizeChoice(18, "18 px"),
        new FontSizeChoice(20, "20 px"),
        new FontSizeChoice(22, "22 px"),
    };

    /// <summary>「機能をペインに前面表示する」ときの配置の振る舞いの選択肢（メイン／サブ／ループ）。</summary>
    public IReadOnlyList<PaneOpenBehaviorChoice> PaneOpenBehaviors { get; } = new[]
    {
        new PaneOpenBehaviorChoice(PaneOpenBehavior.Main, "メイン（左上と入れ替え・従来）"),
        new PaneOpenBehaviorChoice(PaneOpenBehavior.Sub,  "サブ（右上と入れ替え・横1枚なら右へ追加）"),
        new PaneOpenBehaviorChoice(PaneOpenBehavior.Loop, "ループ（サブに表示・サブ起点ならメインへ繰り上げ）"),
    };

    /// <summary>コンボボックスで選択中の各項目。<c>SelectedItem</c> に双方向バインドする。</summary>
    [ObservableProperty] private PresetSwatch _selectedTheme;
    [ObservableProperty] private AccentSwatch? _selectedAccent;
    [ObservableProperty] private PresetSwatch _selectedEditorTheme;
    [ObservableProperty] private PresetSwatch _selectedPreviewTheme;
    [ObservableProperty] private PresetSwatch _selectedTerminalTheme;
    [ObservableProperty] private PaneOpenBehaviorChoice _selectedPaneOpenBehavior;
    [ObservableProperty] private FontSizeChoice _selectedFontSize;

    /// <summary>アクセントカラーの上書き（"#RRGGBB"）。空ならテーマ既定。コンボボックスと任意指定の入力欄で共有する。</summary>
    [ObservableProperty] private string _accentColor = "";

    [ObservableProperty] private string _status = "";

    [ObservableProperty] private string _editorFontFamily = "";
    [ObservableProperty] private string _editorFontSize = "";
    [ObservableProperty] private string _terminalFontFamily = "";
    [ObservableProperty] private string _terminalFontSize = "";
    [ObservableProperty] private bool _terminalFontLigatures;

    /// <summary>アクセント上書きを反映中の再入を防ぐフラグ（コンボ選択 ⇄ AccentColor の往復ループ回避）。</summary>
    private bool _syncingAccent;

    /// <summary>エディタ／プレビュー／ターミナルの配色・フォント設定が変わったときに発火する。
    /// ホスト（ShellWindow）が購読し、開いているタブやプレビューへ即時反映する。</summary>
    public event Action? AppearanceChanged;

    public AppearanceViewModel(AiSettings settings, AiSettingsStore store, ThemeManager themeManager,
        UiFontManager UiFontManager)
    {
        _settings = settings;
        _store = store;
        _themeManager = themeManager;
        _UiFontManager = UiFontManager;

        // チップの色は各テーマの固定代表色（適用中の配色とは独立に表示）。Key は永続化値／照合に使う。
        Themes = new[]
        {
            new PresetSwatch(nameof(AppTheme.Dark),         "ダーク",         "#1E1E1E", "#0E639C", "#D4D4D4"),
            new PresetSwatch(nameof(AppTheme.Light),        "ライト",         "#FFFFFF", "#005FB8", "#1F1F1F"),
            new PresetSwatch(nameof(AppTheme.SolarizedDark),"Solarized",      "#002B36", "#268BD2", "#93A1A1"),
            new PresetSwatch(nameof(AppTheme.Nord),         "Nord",           "#2E3440", "#5E81AC", "#D8DEE9"),
            new PresetSwatch(nameof(AppTheme.HighContrast), "高コントラスト", "#000000", "#1AEBFF", "#FFFFFF"),
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

        // 代表色は ShellWindow.ViewportSplit の各プリセット定義（背景／文字／アクセント・カーソル）と一致させる。
        EditorThemes = new[]
        {
            new PresetSwatch("Dracula",   "Dracula",    "#282A36", "#BD93F9", "#F8F8F2"),
            new PresetSwatch("Dark",      "Dark",       "#1E1E1E", "#569CD6", "#D4D4D4"),
            new PresetSwatch("Nord",      "Nord",       "#2E3440", "#88C0D0", "#D8DEE9"),
            new PresetSwatch("TokyoNight","TokyoNight", "#1A1B26", "#7AA2F7", "#C0CAF5"),
            new PresetSwatch("OneDark",   "OneDark",    "#282C34", "#61AFEF", "#ABB2BF"),
        };

        PreviewThemes = new[]
        {
            new PresetSwatch("Dracula",      "Dracula",      "#282A36", "#8BE9FD", "#F8F8F2"),
            new PresetSwatch("Dark",         "Dark",         "#1E1E1E", "#4FC1FF", "#D4D4D4"),
            new PresetSwatch("Light",        "Light",        "#FFFFFF", "#0969DA", "#24292F"),
            new PresetSwatch("GitHub",       "GitHub",       "#FFFFFF", "#CF222E", "#24292F"),
            new PresetSwatch("Nord",         "Nord",         "#2E3440", "#88C0D0", "#D8DEE9"),
            new PresetSwatch("TokyoNight",   "TokyoNight",   "#1A1B26", "#7AA2F7", "#C0CAF5"),
            new PresetSwatch("OneDark",      "OneDark",      "#282C34", "#61AFEF", "#ABB2BF"),
            new PresetSwatch("SolarizedDark","Solarized",    "#002B36", "#268BD2", "#93A1A1"),
            new PresetSwatch("Monokai",      "Monokai",      "#272822", "#66D9EF", "#F8F8F2"),
        };

        TerminalThemes = new[]
        {
            new PresetSwatch("Dark",         "Dark",      "#1E1E1E", "#5FAFFF", "#D4D4D4"),
            new PresetSwatch("Light",        "Light",     "#FFFFFF", "#0037DA", "#1F1F1F"),
            new PresetSwatch("Dracula",      "Dracula",   "#282A36", "#BD93F9", "#F8F8F2"),
            new PresetSwatch("Nord",         "Nord",      "#2E3440", "#88C0D0", "#D8DEE9"),
            new PresetSwatch("SolarizedDark","Solarized", "#002B36", "#268BD2", "#93A1A1"),
        };

        // 初期選択は背面フィールドへ直接代入（プロパティ setter を介さず OnXxxChanged を発火させない）。
        var ap = settings.Appearance;
        _accentColor = settings.AccentColor ?? "";
        _selectedTheme = Match(Themes, settings.Theme.ToString(), 0);
        _selectedAccent = Accents.FirstOrDefault(a => string.Equals(a.Hex, _accentColor, StringComparison.OrdinalIgnoreCase));
        _selectedEditorTheme = Match(EditorThemes, ap.EditorTheme, 0);
        _selectedPreviewTheme = Match(PreviewThemes, ap.MarkdownPreviewTheme, 0);
        _selectedTerminalTheme = Match(TerminalThemes, ap.TerminalTheme, 0);
        _selectedPaneOpenBehavior = PaneOpenBehaviors.FirstOrDefault(c => c.Value == settings.PaneOpenBehavior)
            ?? PaneOpenBehaviors[0];

        // 現在の基準フォントサイズ（未設定なら既定）に最も近いプリセットを選択に反映する。
        var effectiveSize = UiFontManager.Effective(ap.UiFontSize);
        _selectedFontSize = FontSizes.OrderBy(c => Math.Abs(c.Size - effectiveSize)).First();

        _editorFontFamily = ap.EditorFontFamily ?? "";
        _editorFontSize = ap.EditorFontSize > 0 ? ap.EditorFontSize.ToString("0.#") : "";
        _terminalFontFamily = ap.TerminalFontFamily ?? "";
        _terminalFontSize = ap.TerminalFontSize > 0 ? ap.TerminalFontSize.ToString("0.#") : "";
        _terminalFontLigatures = ap.TerminalFontLigatures;
    }

    /// <summary>Key が保存値と一致する選択肢を返す。無ければ <paramref name="fallbackIndex"/> 番目（既定）。</summary>
    private static PresetSwatch Match(IReadOnlyList<PresetSwatch> choices, string? key, int fallbackIndex) =>
        choices.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? choices[fallbackIndex];

    /// <summary>アプリ全体テーマ：選択を即時適用＆永続化（アクセント上書きは維持される）。</summary>
    partial void OnSelectedThemeChanged(PresetSwatch value)
    {
        if (value is null || !Enum.TryParse<AppTheme>(value.Key, out var theme) || _settings.Theme == theme) return;
        _settings.Theme = theme;
        _themeManager.ApplyTheme(theme);
        Persist("テーマを変更しました");
    }

    /// <summary>アクセント：選択を <see cref="AccentColor"/> 経由で適用する（空=テーマ既定）。</summary>
    partial void OnSelectedAccentChanged(AccentSwatch? value)
    {
        if (_syncingAccent || value is null) return;
        AccentColor = value.Hex;
    }

    partial void OnSelectedEditorThemeChanged(PresetSwatch value)
    {
        if (value is null || string.Equals(_settings.Appearance.EditorTheme, value.Key, StringComparison.Ordinal)) return;
        _settings.Appearance.EditorTheme = value.Key;
        PersistAppearance("エディタのテーマを変更しました");
    }

    partial void OnSelectedPreviewThemeChanged(PresetSwatch value)
    {
        if (value is null || string.Equals(_settings.Appearance.MarkdownPreviewTheme, value.Key, StringComparison.Ordinal)) return;
        _settings.Appearance.MarkdownPreviewTheme = value.Key;
        PersistAppearance("プレビューのテーマを変更しました");
    }

    partial void OnSelectedTerminalThemeChanged(PresetSwatch value)
    {
        if (value is null || string.Equals(_settings.Appearance.TerminalTheme, value.Key, StringComparison.Ordinal)) return;
        _settings.Appearance.TerminalTheme = value.Key;
        PersistAppearance("ターミナルのテーマを変更しました");
    }

    /// <summary>アクセント指定が変わったら検証して即時適用＆永続化。空ならテーマ既定へ戻す。
    /// 適用後はコンボボックスの選択（<see cref="SelectedAccent"/>）を一致するプリセット（無ければ null）へ同期する。</summary>
    partial void OnAccentColorChanged(string value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            _settings.AccentColor = null;
            _themeManager.ApplyAccentColor(null);
            SyncAccentSelection("");
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
        SyncAccentSelection(trimmed);
        Persist("アクセントカラーを変更しました");
    }

    /// <summary>コンボボックスの選択を現在のアクセント値に合わせる（一致が無ければ null＝任意指定中）。
    /// 再入フラグで OnSelectedAccentChanged → AccentColor の往復を防ぐ。</summary>
    private void SyncAccentSelection(string hex)
    {
        _syncingAccent = true;
        SelectedAccent = Accents.FirstOrDefault(a => string.Equals(a.Hex, hex, StringComparison.OrdinalIgnoreCase));
        _syncingAccent = false;
    }

    /// <summary>ペイン前面表示の配置の振る舞い：選択を即時反映＆永続化する（<see cref="AppearanceChanged"/> は不要
    /// ＝次回の前面化から効くので、開いているタブの再描画は起こさない）。</summary>
    partial void OnSelectedPaneOpenBehaviorChanged(PaneOpenBehaviorChoice value)
    {
        if (value is null || _settings.PaneOpenBehavior == value.Value) return;
        _settings.PaneOpenBehavior = value.Value;
        Persist("ペインの表示方法を変更しました");
    }

    /// <summary>アプリ UI の基準フォントサイズ：選択を即時適用＆永続化する。DynamicResource 参照のため
    /// 開いている画面へその場で反映される（エディタ／ターミナルの個別フォント設定とは連動しない）。
    /// 袖／俯瞰カードの名札のようにコードビハインドで <see cref="UiFontManager.Scaled"/> を使う箇所は
    /// DynamicResource が効かないため、<see cref="AppearanceChanged"/> でホストに組み直しを促す。</summary>
    partial void OnSelectedFontSizeChanged(FontSizeChoice value)
    {
        if (value is null || _settings.Appearance.UiFontSize == value.Size) return;
        _settings.Appearance.UiFontSize = value.Size;
        _UiFontManager.Apply(value.Size);
        Persist("文字サイズを変更しました");
        AppearanceChanged?.Invoke();
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

    partial void OnTerminalFontFamilyChanged(string value)
    {
        _settings.Appearance.TerminalFontFamily = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        PersistAppearance("ターミナルのフォントを変更しました");
    }

    partial void OnTerminalFontSizeChanged(string value) =>
        ApplyFontSize(value, v => _settings.Appearance.TerminalFontSize = v,
            () => _settings.Appearance.TerminalFontSize, () => _terminalFontSize,
            v => _terminalFontSize = v, nameof(TerminalFontSize), "ターミナルのフォントサイズを変更しました");

    partial void OnTerminalFontLigaturesChanged(bool value)
    {
        if (_settings.Appearance.TerminalFontLigatures == value) return;
        _settings.Appearance.TerminalFontLigatures = value;
        PersistAppearance(value ? "ターミナルの合字を有効にしました" : "ターミナルの合字を無効にしました");
    }

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

    /// <summary>配色プリセット1つ。コンボボックスの色チップ用の代表色（背景／アクセント／文字）を持つ。
    /// アプリ全体テーマ・エディタ・ターミナル・プレビューで共通。<see cref="Key"/> は永続化値／照合キー。</summary>
    public sealed class PresetSwatch
    {
        public string Key { get; }
        public string Name { get; }
        public string Bg { get; }
        public string Accent { get; }
        public string Fg { get; }

        public PresetSwatch(string key, string name, string bg, string accent, string fg)
        {
            Key = key; Name = name; Bg = bg; Accent = accent; Fg = fg;
        }
    }

    /// <summary>アプリ UI の基準フォントサイズの選択肢1つ（コンボボックス用）。<see cref="Size"/> が永続化値（本文の px）。</summary>
    public sealed class FontSizeChoice
    {
        public double Size { get; }
        public string Name { get; }

        public FontSizeChoice(double size, string name) { Size = size; Name = name; }
    }

    /// <summary>ペイン前面表示の配置の振る舞いの選択肢1つ（コンボボックス用）。<see cref="Value"/> が永続化値。</summary>
    public sealed class PaneOpenBehaviorChoice
    {
        public PaneOpenBehavior Value { get; }
        public string Name { get; }

        public PaneOpenBehaviorChoice(PaneOpenBehavior value, string name) { Value = value; Name = name; }
    }

    /// <summary>アクセントのプリセット1つ。Hex が空文字なら「テーマ既定」。</summary>
    public sealed class AccentSwatch
    {
        public string Name { get; }
        public string Hex { get; }

        public AccentSwatch(string name, string hex) { Name = name; Hex = hex; }
    }
}
