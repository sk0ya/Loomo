using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>自分のテーマの種類。外観パネルの 4 つの選択欄（アプリ全体・エディタ・ターミナル・Markdown プレビュー）と
/// 一対一で、種類ごとに決める色も保存先（<c>%APPDATA%/Loomo/themes/&lt;種類&gt;/&lt;id&gt;.json</c>）も別。</summary>
public enum CustomThemeKind { App, Editor, Terminal, Preview }

/// <summary>テーマの色の欄 1 つ。<see cref="Auto"/> が null の欄は必須（背景と文字）。それ以外は書かなければ
/// <see cref="Auto"/> がほかの欄と背景の明暗から決める。<see cref="MinContrast"/> は背景に対して読める目安
/// （編集画面の注意に使う。null は見ない）。</summary>
public sealed record ThemeSlot(
    string Key, string Label, string Group, string Usage, string Hint,
    Func<ThemeSlotContext, Color>? Auto, double? MinContrast = null)
{
    public bool Required => Auto is null;
}

/// <summary>自動の色を決めるときに見られるもの（背景の明暗とほかの欄の確定した色）。</summary>
public sealed class ThemeSlotContext
{
    private readonly Func<string, Color> _get;
    internal ThemeSlotContext(bool isLight, Func<string, Color> get) { IsLight = isLight; _get = get; }
    public bool IsLight { get; }
    public Color this[string key] => _get(key);
    /// <summary>暗い背景なら <paramref name="dark"/>、明るい背景なら <paramref name="light"/>。</summary>
    public Color Pick(string dark, string light) => CustomThemeFormat.Parse(IsLight ? light : dark);
}

/// <summary>種類ごとの欄の定義。</summary>
public sealed record ThemeSchema(
    CustomThemeKind Kind, string Title, string Folder, IReadOnlyList<ThemeSlot> Slots,
    IReadOnlyDictionary<string, string> GroupDescriptions)
{
    public ThemeSlot? Slot(string key) => Slots.FirstOrDefault(s => s.Key == key);
}

/// <summary>4 種類の欄。各欄の名前・用途は「その色が画面のどこに出るか」をそのまま書く。自動の既定は
/// One Dark / One Light の色相（暗い背景／明るい背景で切り替え）。</summary>
public static class ThemeSchemas
{
    private static Func<ThemeSlotContext, Color> Mix(string a, string b, double t) => c => CustomThemeFormat.Mix(c[a], c[b], t);
    private static Func<ThemeSlotContext, Color> Same(string key) => c => c[key];
    private static Func<ThemeSlotContext, Color> Hue(string dark, string light) => c => c.Pick(dark, light);
    private static Func<ThemeSlotContext, Color> Brighter(string key) =>
        c => CustomThemeFormat.Mix(c[key], c.IsLight ? Colors.Black : Colors.White, c.IsLight ? 0.15 : 0.22);

    private const string Red = "#E06C75", RedL = "#E45649";
    private const string Yellow = "#E5C07B", YellowL = "#C18401";
    private const string Green = "#98C379", GreenL = "#50A14F";
    private const string Cyan = "#56B6C2", CyanL = "#0184BC";
    private const string Blue = "#61AFEF", BlueL = "#4078F2";
    private const string Magenta = "#C678DD", MagentaL = "#A626A4";
    private const string Orange = "#D19A66", OrangeL = "#986801";

    public static readonly ThemeSchema App = new(CustomThemeKind.App, "アプリの画面", "app",
    [
        new("background", "背景", "背景と文字", "パネル・サイドバーの地", "サイドバー・パネル・設定などアプリの画面の地の色", null),
        new("foreground", "文字", "背景と文字", "見出し・一覧の文字", "アプリの画面の文字の色", null, 4.5),
        new("dim", "控えめな文字", "背景と文字", "補足・説明", "補足や説明の文字。自動なら背景と文字の中間", Mix("background", "foreground", 0.55), 3.0),
        new("surface", "一段の面", "背景と文字", "入力欄・帯の地", "入力欄・下の帯・カードなど一段持ち上げた面。自動なら背景を少し文字寄りに", Mix("background", "foreground", 0.05)),
        new("activityBar", "左端の帯", "背景と文字", "アイコンの並ぶ帯", "画面の左端のアイコンが並ぶ帯とタイトルバー", Mix("background", "foreground", 0.10)),
        new("border", "罫線", "背景と文字", "区切り線・枠", "ペインの区切り線や枠", Mix("background", "foreground", 0.16)),
        new("accent", "アクセント", "アクセントと選択", "ボタン・選択中の印", "主要なボタン・選択中の印・ペインの見出しの色味", Hue(Blue, BlueL), 1.6),
        new("selection", "選択行", "アクセントと選択", "一覧で選んだ行", "ツリーや一覧で選んでいる行の下地。自動ならアクセントを背景に薄く混ぜる", c => CustomThemeFormat.SelectionFor(c["background"], c["foreground"], c["accent"])),
        new("searchHighlight", "検索ヒット", "アクセントと選択", "一致した文字の下地", "一覧の絞り込みで一致した文字の下地", Hue(Yellow, YellowL)),
        new("success", "成功", "成功と失敗の印", "テストの合格など", "テストの合格・成功の印", Hue(Green, GreenL), 2.0),
        new("error", "失敗・エラー", "成功と失敗の印", "テストの失敗・エラー", "テストの失敗・エラーの印", Hue(Red, RedL), 2.0),
        new("symType", "型", "構造の一覧の印", "クラス・構造体", "構造の一覧（アウトライン）の型の印", Hue(Yellow, YellowL)),
        new("symMethod", "メソッド", "構造の一覧の印", "関数・メソッド", "構造の一覧のメソッドの印", Hue(Magenta, MagentaL)),
        new("symProperty", "プロパティ", "構造の一覧の印", "プロパティ", "構造の一覧のプロパティの印", Hue(Cyan, CyanL)),
        new("symField", "フィールド", "構造の一覧の印", "フィールド・変数", "構造の一覧のフィールドの印", Hue(Green, GreenL)),
        new("symConstant", "定数", "構造の一覧の印", "定数", "構造の一覧の定数の印", Hue(Orange, OrangeL)),
        new("symInterface", "インターフェイス", "構造の一覧の印", "インターフェイス", "構造の一覧のインターフェイスの印", Same("accent")),
        new("symEnum", "列挙", "構造の一覧の印", "enum", "構造の一覧の列挙の印", Hue(Orange, OrangeL)),
        new("symNamespace", "名前空間", "構造の一覧の印", "namespace", "構造の一覧の名前空間の印", Same("dim")),
    ], new Dictionary<string, string>
    {
        ["背景と文字"] = "サイドバー・パネル・設定などアプリの画面の地と文字です。エディタやターミナルの中の色は、それぞれの「自分で作る」で決めます。",
        ["アクセントと選択"] = "押せる物・選んでいる物に付く色です。",
        ["成功と失敗の印"] = "テストの合否やエラーの表示に出る色です。",
        ["構造の一覧の印"] = "コードの構造（アウトライン）で、種類を見分ける印の色です。",
    });

    public static readonly ThemeSchema Editor = new(CustomThemeKind.Editor, "エディタ", "editor",
    [
        new("background", "背景", "背景と文字", "エディタの地", "エディタの地の色。行番号の帯やカーソルのある行の色もここから作ります", null),
        new("foreground", "文字", "背景と文字", "普通のコード", "色分けされない普通のコードの文字", null, 4.5),
        new("comment", "コメント", "背景と文字", "コメント・行番号", "コメントと行番号の文字。自動なら背景と文字の中間", Mix("background", "foreground", 0.5), 3.0),
        new("accent", "カーソル", "背景と文字", "カーソル・下の帯", "入力中のカーソル・下の状態の帯・リンク", Hue(Blue, BlueL), 1.6),
        new("keyword", "キーワード", "コードの色分け", "class・if・return", "class・if・return など言語の決まった言葉", Hue(Magenta, MagentaL), 2.0),
        new("string", "文字列", "コードの色分け", "\"…\"", "\"…\" で囲んだ文字列", Hue(Green, GreenL), 2.0),
        new("number", "数値", "コードの色分け", "42・true", "数値や true / false などの値", Hue(Orange, OrangeL), 2.0),
        new("type", "型", "コードの色分け", "クラス名・int", "クラス名・型名", Hue(Yellow, YellowL), 2.0),
        new("function", "関数", "コードの色分け", "メソッド名", "関数・メソッドの名前。自動ならカーソルと同じ色", Same("accent"), 2.0),
        new("attribute", "属性", "コードの色分け", "[Fact]・@decorator", "属性・アノテーション・タグの属性名", Hue(Cyan, CyanL), 2.0),
        new("error", "エラー", "エラー・警告・変更の印", "エラーの波線・削除行", "エラーの波線・削除した行の印・置換モードの帯", Hue(Red, RedL), 2.0),
        new("warning", "警告", "エラー・警告・変更の印", "警告・検索・変更行", "警告の波線・検索で一致した所・変更した行の印", Hue(Yellow, YellowL), 2.0),
        new("added", "追加", "エラー・警告・変更の印", "追加行・挿入モード", "追加した行の印・挿入モードの帯", Hue(Green, GreenL), 2.0),
    ], new Dictionary<string, string>
    {
        ["背景と文字"] = "エディタの地と文字です。行番号の帯やカーソルのある行の色も、ここから作ります。",
        ["コードの色分け"] = "コードの種類ごとに付く色です。",
        ["エラー・警告・変更の印"] = "波線・検索ヒット・行の左の変更の印に出る色です。",
    });

    public static readonly ThemeSchema Terminal = new(CustomThemeKind.Terminal, "ターミナル", "terminal",
    [
        new("background", "背景", "背景と文字", "ターミナルの地", "ターミナルの地の色", null),
        new("foreground", "文字", "背景と文字", "色の無い出力", "コマンドが色を付けずに出す普通の文字", null, 4.5),
        new("cursor", "カーソル", "背景と文字", "入力位置", "入力位置のカーソル。自動なら文字と同じ色", Same("foreground")),
        new("selection", "選択", "背景と文字", "ドラッグで選んだ所", "マウスで選んだ範囲の下地", Mix("background", "foreground", 0.25)),
        new("black", "黒", "標準の 8 色", "地に近い文字", "ANSI の黒（0）", c => c.IsLight ? c["foreground"] : CustomThemeFormat.Mix(c["background"], c["foreground"], 0.2)),
        new("red", "赤", "標準の 8 色", "エラー・git の削除", "ANSI の赤（1）", Hue(Red, RedL), 2.0),
        new("green", "緑", "標準の 8 色", "成功・git の追加", "ANSI の緑（2）", Hue(Green, GreenL), 2.0),
        new("yellow", "黄", "標準の 8 色", "警告", "ANSI の黄（3）", Hue(Yellow, YellowL), 2.0),
        new("blue", "青", "標準の 8 色", "ディレクトリ・パス", "ANSI の青（4）", Hue(Blue, BlueL), 2.0),
        new("magenta", "紫", "標準の 8 色", "特別なファイル", "ANSI の紫（5）", Hue(Magenta, MagentaL), 2.0),
        new("cyan", "水色", "標準の 8 色", "情報・リンク", "ANSI の水色（6）", Hue(Cyan, CyanL), 2.0),
        new("white", "白", "標準の 8 色", "明るい文字", "ANSI の白（7）", c => CustomThemeFormat.Mix(c["background"], c["foreground"], c.IsLight ? 0.3 : 0.85)),
        new("brightBlack", "明るい黒（灰）", "明るい 8 色", "控えめな出力", "ANSI の明るい黒（8）。補足やコメントの出力によく使われる", Mix("background", "foreground", 0.45), 2.0),
        new("brightRed", "明るい赤", "明るい 8 色", "強調したエラー", "ANSI の明るい赤（9）", Brighter("red")),
        new("brightGreen", "明るい緑", "明るい 8 色", "強調した成功", "ANSI の明るい緑（10）", Brighter("green")),
        new("brightYellow", "明るい黄", "明るい 8 色", "強調した警告", "ANSI の明るい黄（11）", Brighter("yellow")),
        new("brightBlue", "明るい青", "明るい 8 色", "強調したパス", "ANSI の明るい青（12）", Brighter("blue")),
        new("brightMagenta", "明るい紫", "明るい 8 色", "強調", "ANSI の明るい紫（13）", Brighter("magenta")),
        new("brightCyan", "明るい水色", "明るい 8 色", "強調した情報", "ANSI の明るい水色（14）", Brighter("cyan")),
        new("brightWhite", "明るい白", "明るい 8 色", "いちばん明るい文字", "ANSI の明るい白（15）", c => c.IsLight ? CustomThemeFormat.Mix(c["background"], c["foreground"], 0.12) : c["foreground"]),
    ], new Dictionary<string, string>
    {
        ["背景と文字"] = "色の指定が無い普通の出力の色です。",
        ["標準の 8 色"] = "コマンドが色を付けて出すときの 8 色です（ls のディレクトリ、git の追加・削除、エラーの表示など）。",
        ["明るい 8 色"] = "太字や強調のときに使われる明るい版です。「自動」なら標準の 8 色を少し明るくします。",
    });

    public static readonly ThemeSchema Preview = new(CustomThemeKind.Preview, "Markdown プレビュー", "preview",
    [
        new("background", "背景", "背景と文字", "文書の地", "Markdown プレビューの地の色（JSON・ログ・差分などの表示も同じ）", null),
        new("foreground", "文字", "背景と文字", "本文", "本文の文字", null, 4.5),
        new("dim", "控えめな文字", "背景と文字", "引用・補足", "引用や補足の文字。自動なら背景と文字の中間", Mix("background", "foreground", 0.55), 3.0),
        new("heading", "見出し", "文書の部品", "# 見出し", "見出しの文字", Hue(Magenta, MagentaL), 3.0),
        new("link", "リンク", "文書の部品", "[リンク](…)", "リンクの文字", Hue(Blue, BlueL), 3.0),
        new("strong", "強調", "文書の部品", "**太字**", "太字で強調した文字", Hue(Orange, OrangeL), 3.0),
        new("code", "コードの文字", "文書の部品", "`code`・コード", "インラインのコードとコードブロックの文字", Hue(Green, GreenL), 3.0),
        new("codeBackground", "コードの背景", "文書の部品", "コードブロックの地", "コードブロックとインラインのコードの下地", Mix("background", "foreground", 0.06)),
        new("border", "罫線", "文書の部品", "表の枠・区切り線", "表の枠・区切り線・引用の左の線", Mix("background", "foreground", 0.18)),
        new("red", "赤", "ログ・差分の表示", "エラー・削除行", "ログのエラー・差分の削除行", Hue(Red, RedL), 2.0),
        new("yellow", "黄", "ログ・差分の表示", "警告", "ログの警告", Hue(Yellow, YellowL), 2.0),
        new("green", "緑", "ログ・差分の表示", "追加行", "差分の追加行", Hue(Green, GreenL), 2.0),
    ], new Dictionary<string, string>
    {
        ["背景と文字"] = "文書の地と本文です。JSON・ログ・差分などの表示もこの色で塗ります。",
        ["文書の部品"] = "見出し・リンク・コードなど、文書の部品ごとの色です。",
        ["ログ・差分の表示"] = "ログ（.log）やパッチ（.patch）を表示するときの色です。",
    });

    /// <summary>ターミナルの ANSI 16 色の欄の鍵（0〜15 の順）。<see cref="Terminal"/> の欄の並びから取る。</summary>
    public static IReadOnlyList<string> AnsiKeys { get; } = Terminal.Slots.Skip(4).Select(s => s.Key).ToArray();

    public static ThemeSchema For(CustomThemeKind kind) => kind switch
    {
        CustomThemeKind.App => App,
        CustomThemeKind.Editor => Editor,
        CustomThemeKind.Terminal => Terminal,
        _ => Preview,
    };

    public static IReadOnlyList<ThemeSchema> All { get; } = [App, Editor, Terminal, Preview];
}

/// <summary>読み込んだ自分のテーマ 1 つ。<see cref="Colors"/> は全部の欄が埋まった色（自動の欄も決まっている）。</summary>
public sealed class CustomTheme
{
    public const string KeyPrefix = "user:";

    public required CustomThemeKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsLight { get; init; }
    public required IReadOnlyDictionary<string, Color> Colors { get; init; }
    /// <summary>ファイルに書いてあった欄（それ以外は自動）。</summary>
    public required IReadOnlyDictionary<string, Color> Explicit { get; init; }

    public string Key => KeyPrefix + Id;
    public ThemeSchema Schema => ThemeSchemas.For(Kind);
    public Color this[string key] => Colors[key];
    public string Hex(string key) => CustomThemeFormat.Hex(Colors[key]);
}

/// <summary>テーマファイル（JSONC：コメント・末尾カンマ可）の読み書きと、自動の欄の解決。WPF の Application に
/// 触れない純粋な変換なので、テストからそのまま呼べる。</summary>
public static class CustomThemeFormat
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON を読んでテーマにする。必須は背景と文字だけ。知らない鍵・読めない色は、どの鍵が悪いかを書いた
    /// <see cref="FormatException"/> にする（手で書き換えたときの綴り違いを黙って捨てない）。</summary>
    public static CustomTheme Read(CustomThemeKind kind, string id, string path, string json)
    {
        var schema = ThemeSchemas.For(kind);
        using var doc = JsonDocument.Parse(json, ReadOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("いちばん外側は { } のオブジェクトにしてください");

        var name = id;
        var values = new Dictionary<string, Color>();
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name == "name")
            {
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    name = p.Value.GetString()!.Trim();
                continue;
            }
            var slot = schema.Slots.FirstOrDefault(s => string.Equals(s.Key, p.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"\"{p.Name}\" は{schema.Title}のテーマに無い鍵です");
            if (p.Value.ValueKind == JsonValueKind.Null) continue;
            if (p.Value.ValueKind != JsonValueKind.String || !TryParse(p.Value.GetString(), out var c))
                throw new FormatException(p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim().TrimStart('#').Length == 8
                    ? $"\"{p.Name}\" は 8 桁です。透明度は使えないので \"#RRGGBB\" の 6 桁で書いてください"
                    : $"\"{p.Name}\" の色が読めません（\"#RRGGBB\" で書いてください）");
            values[slot.Key] = c;
        }
        return Resolve(kind, id, path, name, values);
    }

    /// <summary>書いてある色から、自動の欄も含めて全部の欄を決める。</summary>
    public static CustomTheme Resolve(CustomThemeKind kind, string id, string path, string name, IReadOnlyDictionary<string, Color> values)
    {
        var schema = ThemeSchemas.For(kind);
        foreach (var slot in schema.Slots.Where(s => s.Required))
            if (!values.ContainsKey(slot.Key))
                throw new FormatException($"\"{slot.Key}\"（{slot.Label}）が必要です");

        var light = IsLightBackground(values["background"]);
        var resolved = new Dictionary<string, Color>(values);
        var resolving = new HashSet<string>();
        ThemeSlotContext? context = null;
        Color Get(string key)
        {
            if (resolved.TryGetValue(key, out var c)) return c;
            var slot = schema.Slot(key) ?? throw new InvalidOperationException($"欄 {key} が定義にありません");
            if (!resolving.Add(key)) throw new InvalidOperationException($"自動の色が循環しています：{key}");
            var value = slot.Auto!(context!);
            resolving.Remove(key);
            resolved[key] = value;
            return value;
        }
        context = new ThemeSlotContext(light, Get);
        foreach (var slot in schema.Slots) Get(slot.Key);

        return new CustomTheme
        {
            Kind = kind, Id = id, Path = path, Name = name, IsLight = light,
            Colors = resolved.ToImmutableDictionary(),
            Explicit = values.ToImmutableDictionary(),
        };
    }

    /// <summary>ファイルに書く JSONC。書くのは手で決めた欄だけ（自動の欄は書かない＝ほかの色に追従し続ける）。</summary>
    public static string Write(CustomThemeKind kind, string name, IReadOnlyDictionary<string, Color> values)
    {
        var schema = ThemeSchemas.For(kind);
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  // Loomo の自分のテーマ（{schema.Title}）。外観パネルの「自分で作る…」から編集できます。");
        sb.AppendLine("  // 書かない欄は「自動」：背景の明暗とほかの色から決まります。");
        sb.AppendLine($"  \"name\": {JsonSerializer.Serialize(name, WriteOptions)},");
        string? group = null;
        foreach (var slot in schema.Slots)
        {
            if (!values.TryGetValue(slot.Key, out var color)) continue;
            if (group != slot.Group) { sb.AppendLine($"  // {slot.Group}"); group = slot.Group; }
            sb.AppendLine($"  \"{slot.Key}\": \"{Hex(color)}\",   // {slot.Label}（{slot.Usage}）");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ===== 色の小道具 =====

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Parse(string hex) => TryParse(hex, out var c) ? c : System.Windows.Media.Colors.Magenta;

    /// <summary>"#RRGGBB"（"#RGB" も可）を読む。テーマの色は不透明で扱う。8 桁は読まない：VS Code・CSS は
    /// #RRGGBBAA、WPF は #AARRGGBB で、どちらで読んでも片方から貼った色を黙って別の色にしてしまうため。</summary>
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

    public static Color Mix(Color a, Color b, double t)
    {
        byte M(byte x, byte y) => (byte)Math.Clamp(Math.Round(x + (y - x) * t), 0, 255);
        return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    /// <summary>背景が明るいか（<c>PaletteResourceTests</c> の明暗判定と同じ簡易輝度）。</summary>
    public static bool IsLightBackground(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 > 0.5;

    /// <summary>選択行の下地：アクセントを背景に混ぜ、文字とのコントラスト 3.0 を切らない所まで薄める。</summary>
    public static Color SelectionFor(Color background, Color foreground, Color accent)
    {
        var selection = Mix(background, accent, 0.35);
        for (var k = 0.35; k > 0.05 && ThemeManager.Contrast(selection, foreground) < 3.0; k -= 0.02)
            selection = Mix(background, accent, k);
        return selection;
    }
}

/// <summary>種類ごとの下書き（編集画面で手を入れている、まだ保存していないテーマ）。自動の欄は null。
/// プレビューは <see cref="Derive"/>＝保存時と同じ <see cref="CustomThemeFormat.Resolve"/> で作るので、
/// 保存したあとと同じ色が必ず見える。</summary>
public sealed class CustomThemeDraft
{
    public CustomThemeDraft(CustomThemeKind kind)
    {
        Kind = kind;
        Values = ThemeSchemas.For(kind).Slots.ToDictionary(s => s.Key, _ => (Color?)null);
    }

    public CustomThemeKind Kind { get; }
    public string Name { get; set; } = "自分のテーマ";
    public Dictionary<string, Color?> Values { get; }

    public static CustomThemeDraft From(CustomTheme theme)
    {
        var draft = new CustomThemeDraft(theme.Kind) { Name = theme.Name };
        foreach (var (k, v) in theme.Explicit) draft.Values[k] = v;
        return draft;
    }

    public static CustomThemeDraft FromJson(CustomThemeKind kind, string json) =>
        From(CustomThemeFormat.Read(kind, "draft", "", json));

    public CustomThemeDraft Clone()
    {
        var copy = new CustomThemeDraft(Kind) { Name = Name };
        foreach (var (k, v) in Values) copy.Values[k] = v;
        return copy;
    }

    private IReadOnlyDictionary<string, Color> ExplicitValues()
    {
        var values = Values.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.Value);
        // 必須が欠けた下書きは作らない（暗い既定で埋める）。
        values.TryAdd("background", Color.FromRgb(0x1E, 0x1E, 0x1E));
        values.TryAdd("foreground", Color.FromRgb(0xD4, 0xD4, 0xD4));
        return values;
    }

    public string ToJson() => CustomThemeFormat.Write(Kind, Name, ExplicitValues());

    public CustomTheme Derive(string id = "preview") => CustomThemeFormat.Resolve(Kind, id, "", Name, ExplicitValues());
}

/// <summary>読み込んだ自分のテーマの一覧（アプリ全体で 1 つ）。配色の解決口（エディタ・ターミナル・プレビュー）は
/// どれも静的関数なので、ここも静的に持つ。フォルダーを見張り、保存されたら読み直して <see cref="Changed"/> を出す。</summary>
public static class UserThemes
{
    private static ImmutableDictionary<CustomThemeKind, ImmutableArray<CustomTheme>> _all =
        ImmutableDictionary<CustomThemeKind, ImmutableArray<CustomTheme>>.Empty;
    private static ImmutableArray<string> _errors = ImmutableArray<string>.Empty;
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;

    public static string Folder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "themes");

    public static string FolderFor(CustomThemeKind kind) => System.IO.Path.Combine(Folder, ThemeSchemas.For(kind).Folder);

    public static IReadOnlyList<CustomTheme> All(CustomThemeKind kind) =>
        _all.TryGetValue(kind, out var list) ? list : ImmutableArray<CustomTheme>.Empty;

    /// <summary>読めなかったファイルと理由（「種類/名前.json：理由」）。</summary>
    public static IReadOnlyList<string> Errors => _errors;

    /// <summary>読み直したとき（背景スレッドから）。購読側で UI スレッドへ移すこと。</summary>
    public static event Action? Changed;

    public static bool IsUserKey(string? key) =>
        key is not null && key.AsSpan().TrimStart().StartsWith(CustomTheme.KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>選択肢の保存値（<c>user:&lt;id&gt;</c>）からその種類のテーマを引く。それ以外の値・見つからないときは null。</summary>
    public static CustomTheme? TryGet(CustomThemeKind kind, string? key)
    {
        if (!IsUserKey(key)) return null;
        var id = key!.Trim()[CustomTheme.KeyPrefix.Length..];
        return All(kind).FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static void Reload()
    {
        var all = ImmutableDictionary.CreateBuilder<CustomThemeKind, ImmutableArray<CustomTheme>>();
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var schema in ThemeSchemas.All)
        {
            var list = ImmutableArray.CreateBuilder<CustomTheme>();
            var folder = FolderFor(schema.Kind);
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var id = System.IO.Path.GetFileNameWithoutExtension(path);
                    try
                    {
                        list.Add(CustomThemeFormat.Read(schema.Kind, id, path, ReadShared(path)));
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{schema.Folder}/{System.IO.Path.GetFileName(path)}：{FirstLine(ex.Message)}");
                    }
                }
            }
            all[schema.Kind] = list.ToImmutable();
        }
        // 前の形（themes 直下の「部屋全体」テーマ）は読まない。黙って消えたように見えないよう、理由を出す。
        if (Directory.Exists(Folder))
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{System.IO.Path.GetFileName(path)}：前の形（部屋全体を 1 つで塗る）のテーマは読みません。" +
                           "外観パネルの各欄の「自分で作る…」から種類ごとに作り直してください");
        _all = all.ToImmutable();
        _errors = errors.ToImmutable();
    }

    /// <summary>フォルダーの見張りを始める（何度呼んでも 1 つ）。1 回の保存で何度も発火するので、少し待ってまとめて読み直す。</summary>
    public static void StartWatching()
    {
        if (_watcher is not null) return;
        try
        {
            foreach (var schema in ThemeSchemas.All) Directory.CreateDirectory(FolderFor(schema.Kind));
            _debounce = new Timer(_ => { Reload(); Changed?.Invoke(); });
            _watcher = new FileSystemWatcher(Folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
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

/// <summary>アプリの画面のテーマ → パレット（<c>Palette.Dark.xaml</c> と同じキー）。同梱テーマに課している決まり
/// （AccentFg は <see cref="ThemeManager.AccentForeground"/>、ペインヘッダーの帯は実行時と同じ
/// <see cref="ThemeManager.PaneHeaderTint"/>）をここで満たす。</summary>
public static class AppPaletteBuilder
{
    public static IReadOnlyDictionary<string, Color> Build(CustomTheme t)
    {
        var accent = t["accent"];
        var accentFg = ThemeManager.AccentForeground(accent);
        static double L(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        var hover = L(accentFg) > L(accent)
            ? CustomThemeFormat.Mix(accent, Colors.Black, 0.15)
            : CustomThemeFormat.Mix(accent, Colors.White, 0.18);

        var bg = t["background"];
        // 実行時は ThemeManager.ApplyPaneHeader が同じ式で上書きする。ここでもその値にしておけば、
        // 辞書だけで塗る見本（編集画面）も実行時と同じ帯になる。
        var header = ThemeManager.PaneHeaderTint(bg, accent, t["dim"]);

        var search = t["searchHighlight"];
        return new Dictionary<string, Color>
        {
            ["Bg"] = bg,
            ["BgAlt"] = t["surface"],
            ["Panel"] = bg,
            ["ActivityBarBg"] = t["activityBar"],
            ["PaneHeaderBg"] = header,
            ["Border"] = t["border"],
            ["Accent"] = accent,
            ["AccentHover"] = hover,
            ["AccentFg"] = accentFg,
            ["Fg"] = t["foreground"],
            ["FgDim"] = t["dim"],
            ["SearchHighlight"] = Color.FromArgb(0x80, search.R, search.G, search.B),
            ["SelectionBg"] = t["selection"],
            ["Success"] = t["success"],
            ["Error"] = t["error"],
            ["SymNamespace"] = t["symNamespace"],
            ["SymType"] = t["symType"],
            ["SymInterface"] = t["symInterface"],
            ["SymEnum"] = t["symEnum"],
            ["SymMethod"] = t["symMethod"],
            ["SymProperty"] = t["symProperty"],
            ["SymField"] = t["symField"],
            ["SymConstant"] = t["symConstant"],
        };
    }
}
