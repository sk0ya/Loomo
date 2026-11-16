using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>テーマ編集画面で編集中の、まだ保存していないテーマ。基準色（<see cref="Slots"/>）は未指定＝自動を
/// null で持ち、ファイルにあった <c>ui</c> / <c>ansi</c> の直接上書きは編集画面では触らず、そのまま書き戻す。
/// 派生（プレビュー）は <see cref="ToJson"/> を <see cref="UserThemeParser.Parse"/> に通して作るので、
/// 保存したときと同じ色が必ず見える（派生の規則を二重に持たない）。</summary>
public sealed class UserThemeDraft
{
    /// <summary>基準色の欄。並びは編集画面の並び（JSON に書く並びも同じ）。</summary>
    public static readonly IReadOnlyList<SlotInfo> SlotInfos =
    [
        new("background", "背景", "基本", true, "部屋全体の地の色"),
        new("foreground", "文字", "基本", true, "本文の文字色"),
        new("dim", "控えめな文字", "基本", false, "補足・コメント・行番号（自動＝背景と文字の中間）"),
        new("accent", "アクセント", "基本", false, "選択中の印・ボタン・カーソル（自動＝青）"),
        new("red", "赤", "6 色", false, "エラー・削除・ターミナルの赤"),
        new("yellow", "黄", "6 色", false, "警告・検索ヒット・ターミナルの黄"),
        new("green", "緑", "6 色", false, "成功・追加・ターミナルの緑"),
        new("cyan", "水色", "6 色", false, "プロパティ・ターミナルの水色"),
        new("blue", "青", "6 色", false, "リンク・ターミナルの青"),
        new("magenta", "紫", "6 色", false, "ターミナルの紫"),
        new("keyword", "キーワード", "構文", false, "if / class など（自動＝紫）"),
        new("string", "文字列", "構文", false, "\"…\"（自動＝緑）"),
        new("number", "数値", "構文", false, "123・定数（自動＝黄）"),
        new("type", "型", "構文", false, "クラス名・型名（自動＝水色）"),
        new("function", "関数", "構文", false, "関数・メソッド名（自動＝青）"),
    ];

    public sealed record SlotInfo(string Key, string Label, string Group, bool Required, string Hint);

    public string Name { get; set; } = "自分のテーマ";

    /// <summary>基準色。null は自動（明暗に合った既定・ほかの色から派生）。</summary>
    public Dictionary<string, Color?> Slots { get; } = SlotInfos.ToDictionary(s => s.Key, _ => (Color?)null);

    /// <summary>ファイルにあった <c>ui</c> / <c>ansi</c>（JSON の生テキスト）。編集画面では触らずに書き戻す。</summary>
    public string? UiOverridesJson { get; set; }
    public string? AnsiOverridesJson { get; set; }

    public bool HasOverrides => UiOverridesJson is not null || AnsiOverridesJson is not null;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>テーマファイル（JSONC）から読む。読めない色は自動扱い（壊れたファイルでも編集画面で直せるように）。</summary>
    public static UserThemeDraft FromJson(string json)
    {
        var draft = new UserThemeDraft();
        using var doc = JsonDocument.Parse(json, ReadOptions);
        var root = doc.RootElement;
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
            draft.Name = n.GetString()!.Trim();
        foreach (var slot in SlotInfos)
        {
            if (root.TryGetProperty(slot.Key, out var v) && v.ValueKind == JsonValueKind.String
                && UserThemeParser.TryParse(v.GetString(), out var c))
                draft.Slots[slot.Key] = c;
        }
        if (root.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object && ui.EnumerateObject().Any())
            draft.UiOverridesJson = ui.GetRawText();
        if (root.TryGetProperty("ansi", out var ansi) && ansi.ValueKind == JsonValueKind.Array)
            draft.AnsiOverridesJson = ansi.GetRawText();
        return draft;
    }

    public UserThemeDraft Clone()
    {
        var copy = new UserThemeDraft { Name = Name, UiOverridesJson = UiOverridesJson, AnsiOverridesJson = AnsiOverridesJson };
        foreach (var (k, v) in Slots) copy.Slots[k] = v;
        return copy;
    }

    /// <summary>ファイルに書く JSONC。必須の 2 色が欠けていれば暗い既定で埋める（保存できない下書きを作らない）。</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Loomo の自分のテーマ。外観パネルの「自分のテーマを作る・編集する」で編集できます。");
        sb.AppendLine("  // 書かない色（自動）は、背景の明暗に合った既定とほかの色から作ります。");
        sb.AppendLine($"  \"name\": {JsonSerializer.Serialize(Name, WriteOptions)},");
        string? group = null;
        foreach (var slot in SlotInfos)
        {
            var color = Slots[slot.Key] ?? (slot.Key switch
            {
                "background" => Color.FromRgb(0x1E, 0x1E, 0x1E),
                "foreground" => Color.FromRgb(0xD4, 0xD4, 0xD4),
                _ => (Color?)null,
            });
            if (color is null) continue;
            if (group != slot.Group) { sb.AppendLine(); group = slot.Group; }
            sb.AppendLine($"  \"{slot.Key}\": \"{UserTheme.Hex(color.Value)}\",   // {slot.Label}");
        }
        if (UiOverridesJson is not null || AnsiOverridesJson is not null) sb.AppendLine();
        if (UiOverridesJson is not null) sb.AppendLine($"  \"ui\": {UiOverridesJson},");
        if (AnsiOverridesJson is not null) sb.AppendLine($"  \"ansi\": {AnsiOverridesJson},");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>この下書きから派生させたテーマ（プレビュー用。保存したときと同じ規則）。</summary>
    public UserTheme Derive(string id = "preview") => UserThemeParser.Parse(id, "", ToJson());

    /// <summary>派生後のテーマで、その欄に実際に使われる色（自動の欄の見本・ピッカーの初期値に使う）。</summary>
    public static Color Effective(UserTheme t, string key) => key switch
    {
        "background" => t.Bg,
        "foreground" => t.Fg,
        "dim" => t.Dim,
        "accent" => t.Accent,
        "red" => t.Red,
        "yellow" => t.Yellow,
        "green" => t.Green,
        "cyan" => t.Cyan,
        "blue" => t.Blue,
        "magenta" => t.Magenta,
        "keyword" => t.Keyword,
        "string" => t.String,
        "number" => t.Number,
        "type" => t.Type,
        "function" => t.Function,
        _ => Colors.Magenta,
    };

    /// <summary>空いているファイル名（id）。<c>my-theme</c>、<c>my-theme-2</c>…</summary>
    public static string NextFreeId(string folder)
    {
        var id = "my-theme";
        for (var i = 2; File.Exists(Path.Combine(folder, id + ".json")); i++) id = $"my-theme-{i}";
        return id;
    }
}
