using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>利用者が自分で持つカラーテーマ（<c>%APPDATA%/Loomo/themes/&lt;id&gt;.json</c>）。
/// ファイルに書くのは基準色の十数個だけで、アプリ UI のパレット・エディタ・ターミナル（ANSI 16 色）・
/// Markdown 系プレビューの配色はすべてそこから派生させる（同梱テーマのように 7 か所へ書き分けさせない）。
/// 細かく詰めたいところだけ <c>ui</c>（パレットのキー）と <c>ansi</c>（16 色）で上書きできる。
/// 選択肢の保存値は <c>user:&lt;id&gt;</c>（<see cref="KeyPrefix"/>）で、同梱テーマの名前とは衝突しない。</summary>
public sealed class UserTheme
{
    public const string KeyPrefix = "user:";

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Key => KeyPrefix + Id;

    public required bool IsLight { get; init; }
    public required Color Bg { get; init; }
    public required Color Fg { get; init; }
    public required Color Dim { get; init; }
    public required Color Accent { get; init; }
    public required Color Red { get; init; }
    public required Color Yellow { get; init; }
    public required Color Green { get; init; }
    public required Color Cyan { get; init; }
    public required Color Blue { get; init; }
    public required Color Magenta { get; init; }
    public required Color Keyword { get; init; }
    public required Color String { get; init; }
    public required Color Number { get; init; }
    public required Color Type { get; init; }
    public required Color Function { get; init; }

    /// <summary>アプリ UI のパレット（<c>Themes/Palette.Dark.xaml</c> と同じキー）。</summary>
    public required IReadOnlyDictionary<string, Color> Ui { get; init; }

    /// <summary>ターミナルの ANSI 16 色。</summary>
    public required IReadOnlyList<Color> Ansi { get; init; }

    /// <summary>一段持ち上げた面（プレビューのコードブロック等）と罫線。</summary>
    public Color Panel => Ui["BgAlt"];
    public Color Border => Ui["Border"];
    public Color Selection => Ui["SelectionBg"];

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>テーマファイル（JSON）の読み込みと派生。WPF の <see cref="System.Windows.Application"/> には触れない
/// 純粋な変換なので、テストからそのまま呼べる。</summary>
public static class UserThemeParser
{
    /// <summary>パレットのキー（<c>Palette.Dark.xaml</c> と一対一）。<c>ui</c> の上書きはこのキーだけ受け付ける。</summary>
    public static readonly IReadOnlyList<string> PaletteKeys =
    [
        "Bg", "BgAlt", "Panel", "ActivityBarBg", "PaneHeaderBg", "Border", "Accent", "AccentHover", "AccentFg",
        "Fg", "FgDim", "SearchHighlight", "SelectionBg", "Success", "Error",
        "SymNamespace", "SymType", "SymInterface", "SymEnum", "SymMethod", "SymProperty", "SymField", "SymConstant",
    ];

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>JSON を読んでテーマを組み立てる。必須は <c>background</c> と <c>foreground</c> だけ。
    /// 読めない値は理由つきの <see cref="FormatException"/> にする（どのファイルのどこが悪いかを外観パネルに出す）。</summary>
    public static UserTheme Parse(string id, string path, string json)
    {
        using var doc = JsonDocument.Parse(json, Options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("いちばん外側は { } のオブジェクトにしてください");

        Color? Opt(string name)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.String || !TryParse(v.GetString(), out var c))
                throw new FormatException($"\"{name}\" の色が読めません（\"#RRGGBB\" で書いてください）");
            return c;
        }
        Color Req(string name) => Opt(name) ?? throw new FormatException($"\"{name}\" が必要です");

        var bg = Req("background");
        var fg = Req("foreground");
        var light = IsLightBackground(bg);
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()!.Trim() : id;

        // 書かれていない色は明暗に合った既定（One Dark / One Light の色相）で埋める。
        var red = Opt("red") ?? Parse(light ? "#E45649" : "#E06C75");
        var yellow = Opt("yellow") ?? Parse(light ? "#C18401" : "#E5C07B");
        var green = Opt("green") ?? Parse(light ? "#50A14F" : "#98C379");
        var cyan = Opt("cyan") ?? Parse(light ? "#0184BC" : "#56B6C2");
        var blue = Opt("blue") ?? Parse(light ? "#4078F2" : "#61AFEF");
        var magenta = Opt("magenta") ?? Parse(light ? "#A626A4" : "#C678DD");
        var accent = Opt("accent") ?? blue;
        var dim = Opt("dim") ?? Mix(bg, fg, 0.55);

        var theme = new UserTheme
        {
            Id = id,
            Name = name,
            Path = path,
            IsLight = light,
            Bg = bg, Fg = fg, Dim = dim, Accent = accent,
            Red = red, Yellow = yellow, Green = green, Cyan = cyan, Blue = blue, Magenta = magenta,
            Keyword = Opt("keyword") ?? magenta,
            String = Opt("string") ?? green,
            Number = Opt("number") ?? yellow,
            Type = Opt("type") ?? cyan,
            Function = Opt("function") ?? blue,
            Ui = ImmutableDictionary<string, Color>.Empty,
            Ansi = [],
        };

        var ui = DeriveUi(theme).ToDictionary(p => p.Key, p => p.Value);
        if (root.TryGetProperty("ui", out var overrides) && overrides.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in overrides.EnumerateObject())
            {
                var key = PaletteKeys.FirstOrDefault(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FormatException($"\"ui\" の \"{p.Name}\" は知らないキーです（{string.Join(" / ", PaletteKeys)}）");
                if (p.Value.ValueKind != JsonValueKind.String || !TryParse(p.Value.GetString(), out var c))
                    throw new FormatException($"\"ui\" の \"{p.Name}\" の色が読めません");
                ui[key] = c;
            }
        }

        IReadOnlyList<Color> ansi = DeriveAnsi(theme);
        if (root.TryGetProperty("ansi", out var a) && a.ValueKind != JsonValueKind.Null)
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 16)
                throw new FormatException("\"ansi\" は 16 色の配列にしてください（黒 赤 緑 黄 青 紫 水 白 → 明るい側の 8 色）");
            ansi = a.EnumerateArray().Select((e, i) =>
                e.ValueKind == JsonValueKind.String && TryParse(e.GetString(), out var c)
                    ? c : throw new FormatException($"\"ansi\" の {i + 1} 番目の色が読めません")).ToArray();
        }

        return new UserTheme
        {
            Id = theme.Id, Name = theme.Name, Path = theme.Path, IsLight = theme.IsLight,
            Bg = theme.Bg, Fg = theme.Fg, Dim = theme.Dim, Accent = theme.Accent,
            Red = theme.Red, Yellow = theme.Yellow, Green = theme.Green, Cyan = theme.Cyan, Blue = theme.Blue,
            Magenta = theme.Magenta, Keyword = theme.Keyword, String = theme.String, Number = theme.Number,
            Type = theme.Type, Function = theme.Function,
            Ui = ui.ToImmutableDictionary(),
            Ansi = ansi,
        };
    }

    /// <summary>基準色からパレットを作る。同梱テーマと同じ決まり（<c>PaletteResourceTests</c>）を満たすように
    /// 組む：AccentFg は <see cref="ThemeManager.AccentForeground"/>、ペインヘッダーの帯は本体とのコントラスト
    /// 1.18 前後（暗色は明るく・明色は暗く）、選択行は文字とのコントラスト 3.0 以上。</summary>
    private static Dictionary<string, Color> DeriveUi(UserTheme t)
    {
        var accentFg = ThemeManager.AccentForeground(t.Accent);
        // ホバーは Accent 上の文字から遠ざかる向きへ（白文字なら暗く、暗い文字なら明るく）。
        var hover = Luminance(accentFg) > Luminance(t.Accent)
            ? Mix(t.Accent, Colors.Black, 0.15)
            : Mix(t.Accent, Colors.White, 0.18);

        var edge = t.IsLight ? Colors.Black : Colors.White;
        var header = t.Bg;
        for (var k = 0.0; k <= 1.0 && ThemeManager.Contrast(t.Bg, header) < 1.18; k += 0.002)
            header = Mix(t.Bg, edge, k);

        var selection = Mix(t.Bg, t.Accent, 0.35);
        for (var k = 0.35; k > 0.05 && ThemeManager.Contrast(selection, t.Fg) < 3.0; k -= 0.02)
            selection = Mix(t.Bg, t.Accent, k);

        return new Dictionary<string, Color>
        {
            ["Bg"] = t.Bg,
            ["BgAlt"] = Mix(t.Bg, t.Fg, 0.05),
            ["Panel"] = t.Bg,
            ["ActivityBarBg"] = Mix(t.Bg, t.Fg, 0.10),
            ["PaneHeaderBg"] = header,
            ["Border"] = Mix(t.Bg, t.Fg, 0.16),
            ["Accent"] = t.Accent,
            ["AccentHover"] = hover,
            ["AccentFg"] = accentFg,
            ["Fg"] = t.Fg,
            ["FgDim"] = t.Dim,
            ["SearchHighlight"] = Color.FromArgb(0x80, t.Yellow.R, t.Yellow.G, t.Yellow.B),
            ["SelectionBg"] = selection,
            ["Success"] = t.Green,
            ["Error"] = t.Red,
            ["SymNamespace"] = t.Dim,
            ["SymType"] = t.Type,
            ["SymInterface"] = t.Function,
            ["SymEnum"] = t.Yellow,
            ["SymMethod"] = t.Keyword,
            ["SymProperty"] = t.Cyan,
            ["SymField"] = t.Green,
            ["SymConstant"] = t.Number,
        };
    }

    /// <summary>ANSI 16 色。暗色は「黒＝地より少し明るい灰・白＝文字寄り・明るい側は白へ寄せる」、
    /// 明色は「黒＝文字色・白＝地より少し暗い灰・明るい側は黒へ寄せる」（明色の地で bright が飛ばないように）。</summary>
    private static Color[] DeriveAnsi(UserTheme t)
    {
        var hues = new[] { t.Red, t.Green, t.Yellow, t.Blue, t.Magenta, t.Cyan };
        var toward = t.IsLight ? Colors.Black : Colors.White;
        var black = t.IsLight ? t.Fg : Mix(t.Bg, t.Fg, 0.20);
        var white = t.IsLight ? Mix(t.Bg, t.Fg, 0.25) : Mix(t.Bg, t.Fg, 0.85);
        var normal = new[] { black }.Concat(hues).Append(white);
        var bright = new[] { t.Dim }.Concat(hues.Select(h => Mix(h, toward, 0.18))).Append(t.IsLight ? Mix(t.Bg, t.Fg, 0.10) : t.Fg);
        return normal.Concat(bright).ToArray();
    }

    /// <summary>背景が明るいか。<c>PaletteResourceTests</c> の明暗判定と同じ簡易輝度で見る。</summary>
    public static bool IsLightBackground(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 > 0.5;

    public static Color Mix(Color a, Color b, double t)
    {
        byte M(byte x, byte y) => (byte)Math.Clamp(Math.Round(x + (y - x) * t), 0, 255);
        return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Color Parse(string hex) => TryParse(hex, out var c) ? c : Colors.Magenta;

    /// <summary>"#RRGGBB"（"#RGB" も可）を読む。アルファは捨てる（基準色は不透明で扱う）。</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        var s = text?.Trim().TrimStart('#');
        if (s is null) return false;
        if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

/// <summary>読み込んだ利用者テーマの一覧（アプリ全体で 1 つ）。配色の解決口（エディタ・ターミナル・プレビュー）は
/// どれも静的関数なので、ここも静的に持つ。フォルダーを見張り、保存されたら読み直して <see cref="Changed"/> を出す
/// （書いて保存 → その場で部屋の色が変わる、で詰められるように）。</summary>
public static class UserThemes
{
    private static ImmutableArray<UserTheme> _all = ImmutableArray<UserTheme>.Empty;
    private static ImmutableArray<string> _errors = ImmutableArray<string>.Empty;
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;

    public static string Folder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "themes");

    public static IReadOnlyList<UserTheme> All => _all;

    /// <summary>読めなかったファイルと理由（「名前.json：理由」）。</summary>
    public static IReadOnlyList<string> Errors => _errors;

    /// <summary>読み直したとき（背景スレッドから）。購読側で UI スレッドへ移すこと。</summary>
    public static event Action? Changed;

    /// <summary>選択肢の保存値（<c>user:&lt;id&gt;</c>）からテーマを引く。それ以外の値・見つからないときは null。</summary>
    public static UserTheme? TryGet(string? key)
    {
        if (key is null || !key.StartsWith(UserTheme.KeyPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = key[UserTheme.KeyPrefix.Length..];
        return _all.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUserKey(string? key) =>
        key is not null && key.StartsWith(UserTheme.KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>フォルダーの *.json を読み直す。フォルダーが無ければ空。</summary>
    public static void Reload()
    {
        var themes = ImmutableArray.CreateBuilder<UserTheme>();
        var errors = ImmutableArray.CreateBuilder<string>();
        if (Directory.Exists(Folder))
        {
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(path);
                try
                {
                    themes.Add(UserThemeParser.Parse(id, path, ReadShared(path)));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{System.IO.Path.GetFileName(path)}：{FirstLine(ex.Message)}");
                }
            }
        }
        _all = themes.ToImmutable();
        _errors = errors.ToImmutable();
    }

    /// <summary>フォルダーの見張りを始める（何度呼んでも 1 つ）。エディタの保存は「一時ファイルへ書いて置き換え」
    /// のこともあり 1 回の保存で何度も発火するので、少し待ってまとめて読み直す。</summary>
    public static void StartWatching()
    {
        if (_watcher is not null) return;
        try
        {
            Directory.CreateDirectory(Folder);
            _debounce = new Timer(_ => { Reload(); Changed?.Invoke(); });
            _watcher = new FileSystemWatcher(Folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler kick = (_, _) => _debounce.Change(250, Timeout.Infinite);
            _watcher.Changed += kick;
            _watcher.Created += kick;
            _watcher.Deleted += kick;
            _watcher.Renamed += (_, _) => _debounce.Change(250, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 見張れなくても読み込み済みのテーマは使える（保存後の即時反映が効かないだけ）。
        }
    }

    /// <summary>保存中のファイルを共有読みで開く（エディタが書いている最中でも読み直しを落とさない）。</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FirstLine(string message)
    {
        var i = message.IndexOfAny(['\r', '\n']);
        return i < 0 ? message : message[..i];
    }
}
