using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using YamlDotNet.Serialization;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// YAML（.yaml / .yml）を JSON 相当のオブジェクトへパースし、既存の <see cref="JsonTreeRenderer"/> の
/// 折りたたみツリーへそのまま流して表示する EditorSupport 提供者。JSON プレビューと同じ体裁・テーマ・
/// 折りたたみ／絞り込み JS を共有する（見た目を統一しつつ実装コストを最小にする）。表示専用（書き戻しなし）。
/// </summary>
public sealed class YamlEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".yaml", ".yml"];

    public YamlEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"YAML: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => JsonPreviewPage.BuildPage(
            RenderBody(filePath, text), DescribeTitle(filePath), _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text)
        => DataFormatTree.Render(text, DataFormatTree.YamlToJson, "YAML");

    // ページの体裁（対象ファイル・テーマ）だけを鍵にする＝同じファイルを同じテーマで編集している間は
    // #json-root の差し替えだけで更新できる（JsonEditorSupport と同じ方針）。
    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

/// <summary>
/// TOML（.toml）を JSON 相当のオブジェクトへパースし、<see cref="JsonTreeRenderer"/> の折りたたみツリーへ
/// 流して表示する EditorSupport 提供者。<see cref="YamlEditorSupport"/> と同じく JSON プレビューの体裁を共有する。
/// </summary>
public sealed class TomlEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".toml"];

    public TomlEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"TOML: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => JsonPreviewPage.BuildPage(
            RenderBody(filePath, text), DescribeTitle(filePath), _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text)
        => DataFormatTree.Render(text, DataFormatTree.TomlToJson, "TOML");

    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

/// <summary>
/// YAML/TOML → JSON 文字列 → <see cref="JsonTreeRenderer"/> のツリー本文、という共通変換ロジック。
/// 独自にツリー化せず JSON 表現へ寄せることで、JSON プレビューの描画（折りたたみ・絞り込み・値コピー）を
/// そのまま再利用する。編集途中で壊れた入力は例外を投げず、簡潔なエラー本文（原文併記）を返す。
/// </summary>
internal static class DataFormatTree
{
    /// <summary>
    /// <paramref name="text"/> を <paramref name="toJson"/> で JSON 文字列へ変換し、JSON ツリー本文へ落とす。
    /// 空テキストは空表示。変換で例外が出たら（壊れた入力）エラー本文を返す＝呼び出し側へは投げない。
    /// </summary>
    public static string Render(string text, Func<string, string> toJson, string formatLabel)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<div class=\"empty\">（空のファイル）</div>";

        string json;
        try
        {
            json = toJson(text);
        }
        catch (Exception ex)
        {
            return RenderError(text, formatLabel, ex.Message);
        }

        // 変換後は正しい JSON なので JsonTreeRenderer 側でパースエラーになることは通常ない
        // （なってもエラー表示に落ちるだけで例外は出ない）。スキーマ検証は行わない。
        return JsonTreeRenderer.RenderTree(json);
    }

    /// <summary>YAML → JSON 文字列。YamlDotNet の JsonCompatible シリアライザで直変換する（最も堅牢）。</summary>
    public static string YamlToJson(string text)
    {
        // Deserialize&lt;object&gt; は Dictionary&lt;object,object&gt;/List&lt;object&gt; を返す。スカラーは文字列扱いに
        // なるため JSON では数値/真偽値もクォートされるが、表示用途では許容する。
        var obj = new DeserializerBuilder().Build().Deserialize<object?>(text);
        if (obj is null)
            return "null"; // コメントのみ等、内容が空に相当するときは null リーフとして見せる
        return new SerializerBuilder().JsonCompatible().Build().Serialize(obj);
    }

    /// <summary>TOML → JSON 文字列。Toml.Parse で構文検査し、TomlTable を素の .NET オブジェクトへ均してから JSON 化する。</summary>
    public static string TomlToJson(string text)
    {
        var doc = Toml.Parse(text);
        if (doc.HasErrors)
            throw new FormatException(FirstDiagnostic(doc) ?? "TOML の構文エラー");

        var model = doc.ToModel();
        return JsonSerializer.Serialize(ToPlain(model));
    }

    private static string? FirstDiagnostic(Tomlyn.Syntax.DocumentSyntax doc)
    {
        foreach (var diagnostic in doc.Diagnostics)
            return diagnostic.ToString();
        return null;
    }

    // TomlTable/TomlArray/TomlDateTime を、System.Text.Json がそのまま素直に JSON 化できる型へ再帰的に均す。
    // 日時は TomlDateTime.ToString() が TOML 表記そのままの綺麗な文字列を返すのでそれを使う（既定の
    // シリアライズだと {DateTime, SecondPrecision, Kind} という内部構造が漏れてしまう）。
    private static object? ToPlain(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case TomlDateTime dt:
                return dt.ToString();
            case IDictionary<string, object> map: // TomlTable。IEnumerable より先に判定する
                var dict = new Dictionary<string, object?>(map.Count);
                foreach (var kv in map)
                    dict[kv.Key] = ToPlain(kv.Value);
                return dict;
            case IEnumerable items: // TomlArray / TomlTableArray（string は上で処理済み）
                var list = new List<object?>();
                foreach (var item in items)
                    list.Add(ToPlain(item));
                return list;
            default: // long / double / bool など。そのまま JSON 化できる
                return value;
        }
    }

    // JsonTreeRenderer の壊れ JSON 表示に倣った簡潔なエラー本文（見出し＋原文の <pre>）。
    private static string RenderError(string text, string formatLabel, string detail)
    {
        var head = $"{formatLabel} を解析できません";
        if (!string.IsNullOrWhiteSpace(detail))
            head += "：" + detail;
        return "<div class=\"err\"><div class=\"err-head\">" + MarkdownRenderer.Encode(head) + "</div>"
             + "<pre class=\"raw\">" + MarkdownRenderer.Encode(text) + "</pre></div>";
    }
}
