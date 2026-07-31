namespace sk0ya.Loomo.App.Services;

/// <summary>エディタとターミナルへの設定・テーマ適用を一元管理する。</summary>
public sealed class ShellAppearanceCoordinator
{
    private readonly LoomoSettings _settings;
    private readonly Func<Color> _accentColor;
    private readonly Dictionary<VimEditorControl, CancellationTokenSource> _usingFoldRequests = new();
    private readonly object _usingFoldGate = new();

    public ShellAppearanceCoordinator(LoomoSettings settings, Func<Color> accentColor)
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

    /// <summary>C# ファイル読込後、LSP が返す imports 範囲だけを閉じる。</summary>
    public void ApplyUsingFoldingOnOpen(VimEditorControl control)
    {
        if (!_settings.Editor.CollapseUsingsOnOpen
            || !string.Equals(Path.GetExtension(control.FilePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return;

        var filePath = control.FilePath!;
        CancellationTokenSource? previous;
        var request = new CancellationTokenSource();
        lock (_usingFoldGate)
        {
            _usingFoldRequests.Remove(control, out previous);
            _usingFoldRequests[control] = request;
        }
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        _ = CloseUsingFoldWhenAvailableAsync(control, filePath, request);
    }

    private async Task CloseUsingFoldWhenAvailableAsync(
        VimEditorControl control, string filePath, CancellationTokenSource request)
    {
        try
        {
            // LSP の完了通知と FoldManager への反映順序はサーバーごとに異なる。
            // ガターへ実際に現れる既存範囲を最大10秒待ち、それを唯一の真実として閉じる。
            for (var attempt = 0; attempt < 100; attempt++)
            {
                request.Token.ThrowIfCancellationRequested();
                var closed = await control.Dispatcher.InvokeAsync(
                    () => TryCloseExistingUsingFold(control, filePath), DispatcherPriority.ContextIdle);
                if (closed)
                    return;
                await Task.Delay(100, request.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 表示上の補助なので、LSP 停止・ファイル切替時は開いた表示を維持する。
        }
        finally
        {
            lock (_usingFoldGate)
            {
                if (_usingFoldRequests.TryGetValue(control, out var current) && ReferenceEquals(current, request))
                    _usingFoldRequests.Remove(control);
            }
            request.Dispose();
        }
    }

    private bool TryCloseExistingUsingFold(VimEditorControl control, string filePath)
    {
        if (!_settings.Editor.CollapseUsingsOnOpen
            || !string.Equals(control.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return false;

        var allRanges = control.Engine.CurrentBuffer.Folds.Folds
            .Select(fold => new Editor.Core.Lsp.LspFoldingRange(fold.StartLine, fold.EndLine))
            .ToArray();
        var usingRanges = CSharpUsingFoldMatcher.Find(control.Text, allRanges);
        if (usingRanges.Count == 0)
            return false;

        CloseExistingUsingRanges(control, usingRanges);
        return true;
    }

    internal static void CloseExistingUsingRanges(
        VimEditorControl control,
        IReadOnlyList<Editor.Core.Lsp.LspFoldingRange> usingRanges)
    {
        CloseUsingRanges(control.Engine.CurrentBuffer.Folds, usingRanges);

        // FoldManager は Core の状態なので、既存の OptionsChanged 経路で Canvas へ再描画を通知する。
        control.ExecuteCommand($"set {(control.Engine.Options.Number ? "" : "no")}number");
    }

    internal static void CloseUsingRanges(
        Editor.Core.Folds.FoldManager folds,
        IReadOnlyList<Editor.Core.Lsp.LspFoldingRange> usingRanges)
    {
        foreach (var range in usingRanges)
            folds.CloseFold(range.StartLine);
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

    /// <summary>保存名から配色を解決する。ライブラリ内蔵のプリセットを先に見て、無ければ Loomo 側で定義した
    /// <see cref="EditorThemePresets"/>（Monokai・明色系など）を探し、どちらにも無ければ既定の Dracula。</summary>
    internal static EditorTheme ResolveEditorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dark" => EditorTheme.Dark, "nord" => EditorTheme.Nord,
        "tokyonight" => EditorTheme.TokyoNight, "onedark" => EditorTheme.OneDark,
        var other => EditorThemePresets.TryGet(other) ?? EditorTheme.Dracula,
    };

    private static void SetOption(VimEditorControl control, string name, bool value)
        => control.ExecuteCommand($"set {(value ? "" : "no")}{name}");

    private static TerminalColorTheme BuildTerminalColorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "light" => MakeTerminalTheme("#1F1F1F", "#FFFFFF", LightAnsiPalette, "#1F1F1F", "#FFADD1FF"),
        "dracula" => MakeTerminalTheme("#F8F8F2", "#282A36", DraculaAnsiPalette, "#F8F8F0", "#DD5A5E7A"),
        "nord" => MakeTerminalTheme("#D8DEE9", "#2E3440", NordAnsiPalette, "#D8DEE9", "#DD4C566A"),
        "solarizeddark" => MakeTerminalTheme("#93A1A1", "#002B36", SolarizedDarkAnsiPalette, "#93A1A1", "#EE15687F"),
        "tokyonight" => MakeTerminalTheme("#C0CAF5", "#1A1B26", TokyoNightAnsiPalette, "#C0CAF5", "#DD33467C"),
        "onedark" => MakeTerminalTheme("#ABB2BF", "#282C34", OneDarkAnsiPalette, "#61AFEF", "#DD3B5273"),
        "monokai" => MakeTerminalTheme("#F8F8F2", "#272822", MonokaiAnsiPalette, "#F8F8F0", "#DD57564A"),
        "gruvboxdark" => MakeTerminalTheme("#EBDBB2", "#282828", GruvboxDarkAnsiPalette, "#FABD2F", "#DD665C54"),
        "catppuccinmocha" => MakeTerminalTheme("#CDD6F4", "#1E1E2E", CatppuccinMochaAnsiPalette, "#F5E0DC", "#DD45475A"),
        "kanagawa" => MakeTerminalTheme("#DCD7BA", "#1F1F28", KanagawaAnsiPalette, "#C8C093", "#DD2D4F67"),
        "rosepine" => MakeTerminalTheme("#E0DEF4", "#191724", RosePineAnsiPalette, "#E0DEF4", "#DD403D52"),
        "everforestdark" => MakeTerminalTheme("#D3C6AA", "#2D353B", EverforestDarkAnsiPalette, "#D3C6AA", "#DD475258"),
        "nightowl" => MakeTerminalTheme("#D6DEEB", "#011627", NightOwlAnsiPalette, "#80A4C2", "#DD1D3B53"),
        "ayudark" => MakeTerminalTheme("#BFBDB6", "#0B0E14", AyuDarkAnsiPalette, "#E6B450", "#DD1F3A52"),
        "solarizedlight" => MakeTerminalTheme("#586E75", "#FDF6E3", SolarizedLightAnsiPalette, "#586E75", "#FFCFE3EF"),
        "catppuccinlatte" => MakeTerminalTheme("#4C4F69", "#EFF1F5", CatppuccinLatteAnsiPalette, "#DC8A78", "#FFD3DBF5"),
        "rosepinedawn" => MakeTerminalTheme("#575279", "#FAF4ED", RosePineDawnAnsiPalette, "#575279", "#FFDFDAD9"),
        "gruvboxlight" => MakeTerminalTheme("#3C3836", "#FBF1C7", GruvboxLightAnsiPalette, "#3C3836", "#FFDCC79A"),
        "onelight" => MakeTerminalTheme("#383A42", "#FAFAFA", OneLightAnsiPalette, "#4078F2", "#FFD7E4FB"),
        _ => MakeTerminalTheme("#D4D4D4", "#1E1E1E", DarkAnsiPalette, "#5FAFFF", "#DD2E5C8A"),
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
    private static readonly string[] TokyoNightAnsiPalette =
    [
        "#15161E", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
        "#414868", "#FF7A93", "#B9F27C", "#FF9E64", "#7DA6FF", "#BB9AF7", "#0DB9D7", "#C0CAF5"
    ];
    private static readonly string[] OneDarkAnsiPalette =
    [
        "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#ABB2BF",
        "#5C6370", "#EF8A92", "#B2D89A", "#D19A66", "#82C4FF", "#D69BE6", "#7FD1DD", "#FFFFFF"
    ];
    private static readonly string[] MonokaiAnsiPalette =
    [
        "#272822", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F8F8F2",
        "#75715E", "#FF6188", "#BCE651", "#FFD866", "#8FE7F5", "#C4A0FF", "#BDF5EC", "#F9F8F5"
    ];
    private static readonly string[] GruvboxDarkAnsiPalette =
    [
        "#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984",
        "#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2"
    ];
    private static readonly string[] CatppuccinMochaAnsiPalette =
    [
        "#45475A", "#F38BA8", "#A6E3A1", "#F9E2AF", "#89B4FA", "#F5C2E7", "#94E2D5", "#BAC2DE",
        "#585B70", "#FFA0BB", "#B8ECB3", "#FFEEC2", "#A6C8FF", "#FFD4F0", "#A8EEE2", "#A6ADC8"
    ];
    private static readonly string[] KanagawaAnsiPalette =
    [
        "#090618", "#C34043", "#76946A", "#C0A36E", "#7E9CD8", "#957FB8", "#6A9589", "#C8C093",
        "#727169", "#E82424", "#98BB6C", "#E6C384", "#7FB4CA", "#938AA9", "#7AA89F", "#DCD7BA"
    ];
    private static readonly string[] RosePineAnsiPalette =
    [
        "#26233A", "#EB6F92", "#31748F", "#F6C177", "#9CCFD8", "#C4A7E7", "#EBBCBA", "#E0DEF4",
        "#6E6A86", "#FF8FAC", "#3E8FB0", "#FFD79A", "#B4E3EA", "#D6C0F0", "#F5D0CE", "#F0EEFF"
    ];
    private static readonly string[] EverforestDarkAnsiPalette =
    [
        "#343F44", "#E67E80", "#A7C080", "#DBBC7F", "#7FBBB3", "#D699B6", "#83C092", "#D3C6AA",
        "#859289", "#F08C8E", "#B8CE95", "#E5CB94", "#93C9C1", "#E1AAC4", "#95CDA2", "#E2D8C0"
    ];
    private static readonly string[] NightOwlAnsiPalette =
    [
        "#011627", "#EF5350", "#22DA6E", "#ADDB67", "#82AAFF", "#C792EA", "#21C7A8", "#D6DEEB",
        "#575656", "#FF6E67", "#5BFA9E", "#FFEB95", "#A6C4FF", "#DDB2FF", "#7FDBCA", "#FFFFFF"
    ];
    private static readonly string[] AyuDarkAnsiPalette =
    [
        "#1E232B", "#F07178", "#AAD94C", "#FFB454", "#59C2FF", "#D2A6FF", "#39BAE6", "#BFBDB6",
        "#6C7380", "#FF8A91", "#C4EE6E", "#FFD173", "#73B8FF", "#DFBFFF", "#95E6CB", "#FCFCFC"
    ];
    // 明色系ターミナル：白背景でも読めるよう暗色寄りの前景を使う。
    private static readonly string[] SolarizedLightAnsiPalette =
    [
        "#073642", "#DC322F", "#6D8A00", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
        "#002B36", "#CB4B16", "#586E75", "#657B83", "#1E6FA8", "#6C71C4", "#1F8A82", "#93A1A1"
    ];
    private static readonly string[] CatppuccinLatteAnsiPalette =
    [
        "#5C5F77", "#D20F39", "#40A02B", "#DF8E1D", "#1E66F5", "#EA76CB", "#179299", "#ACB0BE",
        "#6C6F85", "#B4082B", "#2F8020", "#B87415", "#1552CC", "#C25CA9", "#0F7379", "#8C90A1"
    ];
    private static readonly string[] RosePineDawnAnsiPalette =
    [
        "#575279", "#B4637A", "#286983", "#9A7414", "#56949F", "#907AA9", "#3E7B7F", "#9893A5",
        "#797593", "#A0576D", "#1F5E77", "#8A6612", "#4A8590", "#7F6B98", "#357071", "#6E6A86"
    ];
    private static readonly string[] GruvboxLightAnsiPalette =
    [
        "#3C3836", "#9D0006", "#79740E", "#B57614", "#076678", "#8F3F71", "#427B58", "#7C6F64",
        "#928374", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#504945"
    ];
    private static readonly string[] OneLightAnsiPalette =
    [
        "#383A42", "#E45649", "#50A14F", "#C18401", "#4078F2", "#A626A4", "#0184BC", "#A0A1A7",
        "#696C77", "#CA1243", "#3F8A3E", "#986801", "#2C5FD0", "#8B1F8A", "#016C99", "#5C5F6B"
    ];
}
