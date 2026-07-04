using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// NDJSON / JSON Lines（.ndjson / .jsonl）を「1行1レコード」の折りたたみツリーで表示する EditorSupport 提供者。
/// 各行の JSON 値を 1 つの JSON 配列 <c>[rec0, rec1, …]</c> へまとめ、既存の <see cref="JsonTreeRenderer"/> の
/// 配列ツリー表示（各要素を <c>N 項目</c>/<c>N 要素</c> で折りたためる）へそのまま流す。JSON プレビューと同じ
/// 体裁・テーマ・折りたたみ／絞り込み JS を共有する。表示専用（書き戻しなし・スキーマ検証なし）。
/// </summary>
public sealed class NdjsonEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".ndjson", ".jsonl"];

    public NdjsonEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"JSONL: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => JsonPreviewPage.BuildPage(
            RenderBody(filePath, text), DescribeTitle(filePath), _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text)
        => JsonTreeRenderer.RenderTree(NdjsonToJsonArray(text));

    // ページの体裁（対象ファイル・テーマ）だけを鍵にする＝同じファイルを同じテーマで編集している間は
    // #json-root の差し替えだけで更新できる（JsonEditorSupport と同じ方針）。
    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);

    /// <summary>1 行 1 レコードのレコード数上限。超過分は捨てて末尾に注記を入れる（巨大ファイルで UI を固めない）。</summary>
    private const int MaxRecords = 50_000;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 256
    };

    /// <summary>
    /// NDJSON テキストを 1 つの JSON 配列文字列へ変換する。各非空行を検証しつつ、<b>行の生テキストをそのまま</b>
    /// 配列要素として連結する（＝各レコードの元の整形を保ったまま 1 配列にまとめ、既存の配列ツリー表示へ流す）。
    /// 空行・空白のみ行はスキップ。壊れた行は全体を捨てず、その行だけをエラーを示す代替オブジェクト
    /// （<c>{"__parse_error__":…,"__raw__":…}</c>）へ置き換えるので、ツリー上でその行だけ問題が分かる。
    /// </summary>
    internal static string NdjsonToJsonArray(string text)
    {
        // \r\n / \n 双方に対応（\r 単独区切りは稀なので考慮しない）。空文字は空配列。
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var sb = new StringBuilder();
        sb.Append("[\n");
        var count = 0;
        var lineNumber = 0;
        var truncated = false;

        foreach (var raw in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (count >= MaxRecords)
            {
                truncated = true;
                break;
            }

            if (count > 0)
                sb.Append(",\n");

            var element = TryValidate(raw, out var error)
                ? raw.Trim()                                   // 正しい行はそのまま（元の整形を保つ）
                : BuildParseErrorElement(lineNumber, error, raw);
            sb.Append("  ").Append(element);
            count++;
        }

        // 上限超過を注記する末尾要素（ツリー上に 1 レコードとして見える）。
        if (truncated)
        {
            if (count > 0)
                sb.Append(",\n");
            sb.Append("  ").Append(BuildNoticeElement(
                $"レコードが多いため先頭 {MaxRecords} 件のみ表示しています"));
            count++;
        }

        // 何も無ければ空配列（JsonTreeRenderer は「[ ]」の空コンテナとして描く）。
        if (count == 0)
            return "[]";

        sb.Append("\n]");
        return sb.ToString();
    }

    /// <summary>1 行分のテキストが正しい JSON 値かを検証する。壊れていれば <paramref name="error"/> にメッセージを入れて false。</summary>
    private static bool TryValidate(string line, out string error)
    {
        try
        {
            using var _ = JsonDocument.Parse(line, ParseOptions);
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>壊れた行をツリー上で示すための代替オブジェクト JSON。文字列は <see cref="JsonSerializer"/> で安全にクオート／エスケープする。</summary>
    private static string BuildParseErrorElement(int lineNumber, string message, string raw)
    {
        var msg = JsonSerializer.Serialize($"{lineNumber}行目: {message}");
        var body = JsonSerializer.Serialize(raw);
        return $"{{ \"__parse_error__\": {msg}, \"__raw__\": {body} }}";
    }

    /// <summary>注記（上限超過など）をツリー上で示すための代替オブジェクト JSON。</summary>
    private static string BuildNoticeElement(string message)
        => $"{{ \"__notice__\": {JsonSerializer.Serialize(message)} }}";
}
