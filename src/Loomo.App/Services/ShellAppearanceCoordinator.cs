namespace sk0ya.Loomo.App.Services;

/// <summary>エディタとターミナルへの設定・テーマ適用を一元管理する。</summary>
public sealed class ShellAppearanceCoordinator
{
    private readonly AiSettings _settings;
    private readonly Func<Color> _accentColor;

    public ShellAppearanceCoordinator(AiSettings settings, Func<Color> accentColor)
    {
        _settings = settings;
        _accentColor = accentColor;
    }

    public void ApplyEditorOptions(VimEditorControl control)
    {
        var settings = _settings.Editor;
        control.Engine.Options.HighlightWhitespace = settings.HighlightWhitespace;
        control.InvalidateVisual();
        SetOption(control, "number", settings.ShowLineNumbers);
        SetOption(control, "relativenumber", settings.RelativeLineNumbers);
        SetOption(control, "cursorline", settings.HighlightCurrentLine);
        SetOption(control, "wrap", settings.WordWrap);
        SetOption(control, "minimap", settings.ShowMinimap);
        SetOption(control, "indentguides", settings.ShowIndentGuides);
        SetOption(control, "pairs", settings.AutoClosePairs);
        control.SetTabWidth(settings.TabWidth, settings.UseSpacesForTab);
        control.ImagePasteOptions = new Editor.Core.Editing.ImagePasteOptions
        {
            Directory = settings.ImagePasteDirectory,
            FileName = settings.ImagePasteFileName,
            AltText = settings.ImagePasteAltText
        };
    }

    public void ApplyEditorAppearance(VimEditorControl control)
    {
        control.SetTheme(BuildEditorTheme());
        var appearance = _settings.Appearance;
        if (!string.IsNullOrWhiteSpace(appearance.EditorFontFamily))
            control.EditorFontFamily = appearance.EditorFontFamily;
        if (appearance.EditorFontSize > 0)
            control.EditorFontSize = appearance.EditorFontSize;
    }

    public void ApplyTerminalAppearance(TerminalTabView view)
    {
        var appearance = _settings.Appearance;
        view.SetColorTheme(BuildTerminalColorTheme(appearance.TerminalTheme));
        var family = string.IsNullOrWhiteSpace(appearance.TerminalFontFamily)
            ? view.FontFamilyName : appearance.TerminalFontFamily;
        var size = appearance.TerminalFontSize > 0 ? appearance.TerminalFontSize : view.TerminalFontSize;
        view.SetFont(family, size);
        view.SetFontLigaturesEnabled(appearance.TerminalFontLigatures);
    }

    internal EditorTheme BuildEditorTheme()
    {
        var accent = _accentColor();
        var selection = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B));
        var baseTheme = ResolveEditorTheme(_settings.Appearance.EditorTheme);
        var clone = new EditorTheme();
        foreach (var property in typeof(EditorTheme).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite)
                continue;
            property.SetValue(clone,
                property.Name == nameof(EditorTheme.SelectionBg) ? selection : property.GetValue(baseTheme));
        }
        return clone;
    }

    internal static EditorTheme ResolveEditorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dark" => EditorTheme.Dark, "nord" => EditorTheme.Nord,
        "tokyonight" => EditorTheme.TokyoNight, "onedark" => EditorTheme.OneDark,
        _ => EditorTheme.Dracula,
    };

    private static void SetOption(VimEditorControl control, string name, bool value)
        => control.ExecuteCommand($"set {(value ? "" : "no")}{name}");

    private static TerminalColorTheme BuildTerminalColorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "light" => MakeTerminalTheme("#1F1F1F", "#FFFFFF", LightAnsiPalette, "#1F1F1F", "#FFB3D7FF"),
        "dracula" => MakeTerminalTheme("#F8F8F2", "#282A36", DraculaAnsiPalette, "#F8F8F0", "#6644475A"),
        "nord" => MakeTerminalTheme("#D8DEE9", "#2E3440", NordAnsiPalette, "#D8DEE9", "#66434C5E"),
        "solarizeddark" => MakeTerminalTheme("#93A1A1", "#002B36", SolarizedDarkAnsiPalette, "#93A1A1", "#66073642"),
        _ => MakeTerminalTheme("#D4D4D4", "#1E1E1E", DarkAnsiPalette, "#5FAFFF", "#664D4D4D"),
    };

    private static TerminalColorTheme MakeTerminalTheme(
        string foreground, string background, string[] palette, string cursor, string selection) =>
        new(ParseColor(foreground), ParseColor(background), palette.Select(ParseColor).ToArray(),
            ParseColor(cursor), ParseColor(selection));

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static readonly string[] DarkAnsiPalette =
    [
        "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
        "#9D9D9D", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
    ];
    private static readonly string[] LightAnsiPalette =
    [
        "#000000", "#C50F1F", "#13A10E", "#B58900", "#0037DA", "#881798", "#3A96DD", "#777777",
        "#5A5A5A", "#A4262C", "#0E8016", "#986801", "#0037DA", "#A100A1", "#178C92", "#1F1F1F"
    ];
    private static readonly string[] DraculaAnsiPalette =
    [
        "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
        "#8A95C2", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"
    ];
    private static readonly string[] NordAnsiPalette =
    [
        "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
        "#909FBB", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"
    ];
    private static readonly string[] SolarizedDarkAnsiPalette =
    [
        "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
        "#839496", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"
    ];
}
