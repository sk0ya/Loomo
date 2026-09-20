namespace sk0ya.Loomo.App.Services;

/// <summary>エディタの配色プリセットのうち、ライブラリ（<see cref="EditorTheme"/> の静的プロパティ）が
/// 持っていないものを Loomo 側で定義する。<see cref="EditorTheme"/> は全プロパティが公開の init セッターを
/// 持つただのクラスなので、ライブラリの更新を待たずにテーマを増やせる。
///
/// 各テーマは「基準色（<see cref="Palette"/>）だけを列挙し、残りは派生させる」方式にする。ライブラリ内蔵
/// テーマのように50個近いブラシを1つずつ書き並べるとテーマ追加のたびに書き漏らしが出るため、行番号・
/// カレント行・インデントガイドなどの地味な色は背景と文字色の混色（<see cref="Mix"/>）で機械的に決める。
/// これにより明色系テーマ（Light / Solarized Light / Catppuccin Latte）でも自動で正しい方向へ寄る。
/// なお選択範囲（SelectionBg）は <see cref="ShellAppearanceCoordinator.BuildEditorTheme"/> がアクセント色で
/// 上書きするため、ここでの値は使われない。</summary>
internal static class EditorThemePresets
{
    /// <summary>名前（小文字化済み）に対応するプリセットを返す。無ければ null（＝ライブラリ側／既定に委ねる）。</summary>
    internal static EditorTheme? TryGet(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "solarizeddark" => SolarizedDark,
        "monokai" => Monokai,
        "gruvboxdark" => GruvboxDark,
        "catppuccinmocha" => CatppuccinMocha,
        "kanagawa" => Kanagawa,
        "rosepine" => RosePine,
        "everforestdark" => EverforestDark,
        "nightowl" => NightOwl,
        "ayudark" => AyuDark,
        "highcontrast" => HighContrast,
        "light" => Light,
        "solarizedlight" => SolarizedLight,
        "catppuccinlatte" => CatppuccinLatte,
        "rosepinedawn" => RosePineDawn,
        "gruvboxlight" => GruvboxLight,
        "onelight" => OneLight,
        _ => null,
    };

    /// <summary>プリセットの基準色。Dim は行番号・コメント、Accent はカーソル・関数名・ステータスバー、
    /// Red/Yellow/Green は診断・git差分・強調（検索/全角空白/行末空白）に使う。</summary>
    private sealed record Palette(
        string Bg, string Fg, string Dim, string Accent,
        string Keyword, string Str, string Number, string Type, string Attribute,
        string Red, string Yellow, string Green);

    private static readonly EditorTheme SolarizedDark = Build(new Palette(
        Bg: "#002B36", Fg: "#93A1A1", Dim: "#586E75", Accent: "#268BD2",
        Keyword: "#859900", Str: "#2AA198", Number: "#D33682", Type: "#B58900", Attribute: "#6C71C4",
        Red: "#DC322F", Yellow: "#B58900", Green: "#859900"));

    private static readonly EditorTheme Monokai = Build(new Palette(
        Bg: "#272822", Fg: "#F8F8F2", Dim: "#75715E", Accent: "#66D9EF",
        Keyword: "#F92672", Str: "#E6DB74", Number: "#AE81FF", Type: "#66D9EF", Attribute: "#A6E22E",
        Red: "#F92672", Yellow: "#E6DB74", Green: "#A6E22E"));

    private static readonly EditorTheme GruvboxDark = Build(new Palette(
        Bg: "#282828", Fg: "#EBDBB2", Dim: "#928374", Accent: "#83A598",
        Keyword: "#FB4934", Str: "#B8BB26", Number: "#D3869B", Type: "#FABD2F", Attribute: "#8EC07C",
        Red: "#FB4934", Yellow: "#FABD2F", Green: "#B8BB26"));

    private static readonly EditorTheme CatppuccinMocha = Build(new Palette(
        Bg: "#1E1E2E", Fg: "#CDD6F4", Dim: "#6C7086", Accent: "#89B4FA",
        Keyword: "#CBA6F7", Str: "#A6E3A1", Number: "#FAB387", Type: "#F9E2AF", Attribute: "#89DCEB",
        Red: "#F38BA8", Yellow: "#F9E2AF", Green: "#A6E3A1"));

    private static readonly EditorTheme Kanagawa = Build(new Palette(
        Bg: "#1F1F28", Fg: "#DCD7BA", Dim: "#9C9A8E", Accent: "#7E9CD8",
        Keyword: "#957FB8", Str: "#98BB6C", Number: "#D27E99", Type: "#7AA89F", Attribute: "#7FB4CA",
        Red: "#E46876", Yellow: "#C0A36E", Green: "#98BB6C"));

    private static readonly EditorTheme RosePine = Build(new Palette(
        Bg: "#191724", Fg: "#E0DEF4", Dim: "#908CAA", Accent: "#C4A7E7",
        Keyword: "#31748F", Str: "#F6C177", Number: "#EBBCBA", Type: "#9CCFD8", Attribute: "#C4A7E7",
        Red: "#EB6F92", Yellow: "#F6C177", Green: "#9CCFD8"));

    private static readonly EditorTheme EverforestDark = Build(new Palette(
        Bg: "#2D353B", Fg: "#D3C6AA", Dim: "#859289", Accent: "#A7C080",
        Keyword: "#E67E80", Str: "#A7C080", Number: "#D699B6", Type: "#DBBC7F", Attribute: "#83C092",
        Red: "#E67E80", Yellow: "#DBBC7F", Green: "#A7C080"));

    private static readonly EditorTheme NightOwl = Build(new Palette(
        Bg: "#011627", Fg: "#D6DEEB", Dim: "#8BA1B1", Accent: "#82AAFF",
        Keyword: "#C792EA", Str: "#ECC48D", Number: "#F78C6C", Type: "#7FDBCA", Attribute: "#ADDB67",
        Red: "#EF5350", Yellow: "#ECC48D", Green: "#22DA6E"));

    private static readonly EditorTheme AyuDark = Build(new Palette(
        Bg: "#0B0E14", Fg: "#BFBDB6", Dim: "#8A9199", Accent: "#E6B450",
        Keyword: "#FFB454", Str: "#AAD94C", Number: "#D2A6FF", Type: "#59C2FF", Attribute: "#39BAE6",
        Red: "#F07178", Yellow: "#E6B450", Green: "#AAD94C"));

    // 高コントラスト：背景は純黒、コメント（Dim）も読める明度まで上げる。
    private static readonly EditorTheme HighContrast = Build(new Palette(
        Bg: "#000000", Fg: "#FFFFFF", Dim: "#B4B4B4", Accent: "#1AEBFF",
        Keyword: "#6FC3FF", Str: "#FFC68A", Number: "#A5FFB0", Type: "#4EE6C0", Attribute: "#E0E0E0",
        Red: "#FF6B6B", Yellow: "#FFD700", Green: "#4CFF4C"));

    private static readonly EditorTheme Light = Build(new Palette(
        Bg: "#FFFFFF", Fg: "#1F1F1F", Dim: "#6E7781", Accent: "#005FB8",
        Keyword: "#0000FF", Str: "#A31515", Number: "#098658", Type: "#267F99", Attribute: "#001080",
        Red: "#D13438", Yellow: "#BF8700", Green: "#107C10"));

    private static readonly EditorTheme SolarizedLight = Build(new Palette(
        Bg: "#FDF6E3", Fg: "#586E75", Dim: "#93A1A1", Accent: "#1E76B8",
        Keyword: "#859900", Str: "#2AA198", Number: "#D33682", Type: "#B58900", Attribute: "#6C71C4",
        Red: "#DC322F", Yellow: "#B58900", Green: "#859900"));

    private static readonly EditorTheme CatppuccinLatte = Build(new Palette(
        Bg: "#EFF1F5", Fg: "#4C4F69", Dim: "#8C8FA1", Accent: "#1E66F5",
        Keyword: "#8839EF", Str: "#40A02B", Number: "#FE640B", Type: "#DF8E1D", Attribute: "#04A5E5",
        Red: "#D20F39", Yellow: "#DF8E1D", Green: "#40A02B"));

    private static readonly EditorTheme RosePineDawn = Build(new Palette(
        Bg: "#FAF4ED", Fg: "#575279", Dim: "#797593", Accent: "#907AA9",
        Keyword: "#286983", Str: "#9A7414", Number: "#B4637A", Type: "#3E7B7F", Attribute: "#907AA9",
        Red: "#B4637A", Yellow: "#9A7414", Green: "#3E7B7F"));

    private static readonly EditorTheme GruvboxLight = Build(new Palette(
        Bg: "#FBF1C7", Fg: "#3C3836", Dim: "#7C6F64", Accent: "#B57614",
        Keyword: "#9D0006", Str: "#79740E", Number: "#8F3F71", Type: "#B57614", Attribute: "#427B58",
        Red: "#9D0006", Yellow: "#B57614", Green: "#79740E"));

    private static readonly EditorTheme OneLight = Build(new Palette(
        Bg: "#FAFAFA", Fg: "#383A42", Dim: "#7C7F8A", Accent: "#4078F2",
        Keyword: "#A626A4", Str: "#50A14F", Number: "#986801", Type: "#C18401", Attribute: "#0184BC",
        Red: "#E45649", Yellow: "#C18401", Green: "#50A14F"));

    /// <summary>基準色から <see cref="EditorTheme"/> を組み立てる。地の色は背景←→文字色の混色で派生させ、
    /// ステータスバーの文字色はアクセントの明度から黒／白を選ぶ（明色アクセントでも文字が沈まないように）。</summary>
    private static EditorTheme Build(Palette p)
    {
        var bg = Parse(p.Bg);
        var fg = Parse(p.Fg);
        var accent = Parse(p.Accent);
        var red = Parse(p.Red);
        var yellow = Parse(p.Yellow);
        var green = Parse(p.Green);
        var dark = Luminance(bg) < 0.5;

        return new EditorTheme
        {
            Background = Solid(bg),
            Foreground = Solid(fg),
            CursorBackground = Solid(fg),
            CursorForeground = Solid(bg),
            InsertCursor = Solid(accent),
            LineNumberFg = Brush(p.Dim),
            CurrentLineNumberFg = Solid(fg),
            LineNumberBg = Solid(Shade(bg, dark ? -8 : -6)),
            CurrentLineBg = Solid(Mix(bg, fg, 0.10)),
            SelectionBg = Solid(accent, 0x66),
            SearchHighlightBg = Solid(yellow, 0xA0),
            StatusBarNormal = Solid(accent),
            StatusBarInsert = Solid(green),
            StatusBarVisual = Solid(yellow),
            StatusBarReplace = Solid(red),
            StatusBarFg = Solid(Luminance(accent) > 0.55 ? Parse("#101010") : Parse("#FFFFFF")),

            GitAdded = Solid(green),
            GitModified = Solid(yellow),
            GitDeleted = Solid(red),

            MatchingBracketBackground = Solid(accent, 0x66),
            DocumentHighlightBackground = Solid(accent, 0x44),
            ColorColumnBrush = Solid(Mix(bg, fg, 0.16)),
            ListCharBrush = Solid(Mix(bg, fg, 0.32)),
            IndentGuideBrush = Solid(Mix(bg, fg, 0.20)),
            FullWidthSpaceBackground = Solid(yellow, 0x50),
            TrailingWhitespaceBackground = Solid(red, 0x50),

            ConflictOursHeader = Solid(red, 0x55),
            ConflictSeparator = Solid(yellow, 0x55),
            ConflictTheirsHeader = Solid(accent, 0x55),
            ConflictOurs = Solid(red, 0x22),
            ConflictTheirs = Solid(accent, 0x22),

            ScrollbarTrack = Solid(fg, 0x18),
            ScrollbarThumb = Solid(fg, 0x50),
            MinimapBackground = Solid(Mix(bg, fg, 0.04), 0xD0),
            MinimapViewport = Solid(accent, 0x55),
            LinkColor = Solid(accent),

            DiagnosticError = Solid(red),
            DiagnosticWarning = Solid(yellow),
            DiagnosticInfo = Solid(accent),
            DiagnosticHint = Brush(p.Dim),

            TokenKeyword = Brush(p.Keyword),
            TokenString = Brush(p.Str),
            TokenComment = Brush(p.Dim),
            TokenNumber = Brush(p.Number),
            TokenPreprocessor = Brush(p.Dim),
            TokenType = Brush(p.Type),
            TokenAttribute = Brush(p.Attribute),
            TokenIdentifier = Solid(fg),
            TokenFunction = Solid(accent),
        };
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static SolidColorBrush Brush(string hex) => Solid(Parse(hex));

    /// <summary>凍結済みブラシを作る（プリセットは静的に共有され、UIスレッド外からも読まれ得るため）。</summary>
    private static SolidColorBrush Solid(Color color, byte alpha = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary><paramref name="from"/> を <paramref name="to"/> 方向へ <paramref name="ratio"/> だけ混ぜる。
    /// 背景←→文字色の混色なので、暗色テーマでは明るく、明色テーマでは暗くなる。</summary>
    private static Color Mix(Color from, Color to, double ratio) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * ratio),
        (byte)(from.G + (to.G - from.G) * ratio),
        (byte)(from.B + (to.B - from.B) * ratio));

    private static Color Shade(Color color, int delta) => Color.FromRgb(
        Clamp(color.R + delta), Clamp(color.G + delta), Clamp(color.B + delta));

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>相対的な明るさ（0=黒〜1=白）。明暗どちらのテーマかの判定と文字色選択に使う。</summary>
    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
