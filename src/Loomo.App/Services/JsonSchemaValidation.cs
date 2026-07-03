using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace sk0ya.Loomo.App.Services;

/// <summary>スキーマ違反1件（ツリーの JSONパス表記 + メッセージ）。</summary>
public sealed record JsonSchemaError(string Path, string Message);

/// <summary>JSON スキーマ検証の結果。<see cref="SchemaFound"/> が false のときは検証していない（バッジも出さない）。</summary>
public sealed record JsonValidationResult(bool SchemaFound, IReadOnlyList<JsonSchemaError> Errors)
{
    public static readonly JsonValidationResult None = new(false, Array.Empty<JsonSchemaError>());
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// JSON プレビューのスキーマ検証。対象ファイルに紐づくスキーマを解決し、本文を検証して違反を返す。
/// スキーマの取得元は次の順で解決する（見つからなければ検証しない）。
/// <list type="number">
/// <item>文書の <c>$schema</c> がローカルパス／相対／<c>file://</c> を指す → そのファイル。</item>
/// <item>文書の <c>$schema</c> が <c>http(s)</c> → 取得して <c>%APPDATA%/Loomo/schema-cache</c> にキャッシュ。</item>
/// <item>兄弟ファイル <c>&lt;名前&gt;.schema.json</c>。</item>
/// </list>
/// 検証は <see cref="JsonEditorSupport"/> のバックグラウンド変換内から同期的に呼ばれる（HTTP はブロッキング）。
/// コンパイル済みスキーマはメモリにキャッシュし（ローカルは更新時刻、URL は URL で鍵）、打鍵ごとの再取得を避ける。
/// </summary>
public sealed class JsonSchemaValidator
{
    // 巨大ファイルで打鍵を固めないための上限（超えたら検証しない）。
    private const int MaxValidateChars = 2_000_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "schema-cache");

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 256,
    };

    // コンパイル済みスキーマのキャッシュ（鍵: "file:<path>:<mtime>" / "url:<url>"）。
    private readonly ConcurrentDictionary<string, JsonSchema> _compiled = new();

    /// <summary>対象ファイルの本文を、解決できたスキーマで検証する。失敗・スキーマ無しは <see cref="JsonValidationResult.None"/>。</summary>
    public JsonValidationResult Validate(string? filePath, string text)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(text) || text.Length > MaxValidateChars)
            return JsonValidationResult.None;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, DocOptions);
        }
        catch (JsonException)
        {
            return JsonValidationResult.None; // 編集途中の壊れた JSON はツリー側がパースエラーを出す。
        }

        using (doc)
        {
            var root = doc.RootElement;

            JsonSchema? schema;
            try
            {
                schema = ResolveSchema(filePath, root);
            }
            catch
            {
                return JsonValidationResult.None; // スキーマ取得・コンパイル失敗（ネット断・不正スキーマ等）は検証しない。
            }
            if (schema is null)
                return JsonValidationResult.None;

            try
            {
                var results = schema.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
                if (results.IsValid)
                    return new JsonValidationResult(true, Array.Empty<JsonSchemaError>());

                var errors = new List<JsonSchemaError>();
                CollectErrors(results, root, errors);
                // 同一パスの重複はまとめる（同じ場所に複数キーワード違反が出ることがある）。
                var merged = errors
                    .GroupBy(e => e.Path)
                    .Select(g => new JsonSchemaError(g.Key, string.Join(" / ", g.Select(x => x.Message).Distinct())))
                    .ToList();
                return new JsonValidationResult(true, merged);
            }
            catch
            {
                return JsonValidationResult.None; // 外部 $ref 解決不能などの評価時例外は検証扱いにしない。
            }
        }
    }

    private static void CollectErrors(EvaluationResults results, JsonElement root, List<JsonSchemaError> sink)
    {
        if (results.Errors is { Count: > 0 } errs)
        {
            var path = PointerToPath(root, results.InstanceLocation);
            foreach (var msg in errs.Values)
                if (!string.IsNullOrWhiteSpace(msg))
                    sink.Add(new JsonSchemaError(path, msg));
        }

        if (results.Details is { } details)
            foreach (var child in details)
                CollectErrors(child, root, sink);
    }

    /// <summary>スキーマ違反の instanceLocation（JSON Pointer）を、ツリーの data-path 表記（$.a.b[0]）へ変換する。</summary>
    private static string PointerToPath(JsonElement root, Json.Pointer.JsonPointer pointer)
    {
        var sb = new StringBuilder("$");
        var cur = root;
        var alive = true;
        for (var i = 0; i < pointer.SegmentCount; i++)
        {
            var key = pointer[i].ToString();
            if (alive && cur.ValueKind == JsonValueKind.Array && int.TryParse(key, out var idx))
            {
                sb.Append('[').Append(idx).Append(']');
                if (idx >= 0 && idx < cur.GetArrayLength())
                    cur = cur[idx];
                else
                    alive = false;
            }
            else if (alive && cur.ValueKind == JsonValueKind.Object)
            {
                sb.Append(JsonTreeRenderer.Accessor(key));
                if (cur.TryGetProperty(key, out var v))
                    cur = v;
                else
                    alive = false;
            }
            else
            {
                sb.Append(JsonTreeRenderer.Accessor(key));
                alive = false;
            }
        }
        return sb.ToString();
    }

    // --- スキーマ解決 ---------------------------------------------------------

    private JsonSchema? ResolveSchema(string filePath, JsonElement node)
    {
        var reference = ReadSchemaRef(node);
        if (reference is not null)
        {
            if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                    return CompileLocal(uri.LocalPath);
                if (uri.Scheme is "http" or "https")
                    return CompileRemote(uri.AbsoluteUri);
                // その他スキーム（json-schema.org のダイアレクト等の非取得 URI）は $schema 由来では追わない。
            }
            else
            {
                // 相対パス（"./config.schema.json" 等）は対象ファイルのフォルダ基準で解決。
                var dir = Path.GetDirectoryName(filePath);
                if (dir is not null)
                {
                    var local = Path.GetFullPath(Path.Combine(dir, reference));
                    if (File.Exists(local))
                        return CompileLocal(local);
                }
            }
        }

        // 兄弟の <名前>.schema.json（自分自身が *.schema.json のときは除く）。
        if (!filePath.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(filePath) ?? "",
                Path.GetFileNameWithoutExtension(filePath) + ".schema.json");
            if (File.Exists(sibling))
                return CompileLocal(sibling);
        }

        return null;
    }

    private static string? ReadSchemaRef(JsonElement node)
        => node.ValueKind == JsonValueKind.Object
           && node.TryGetProperty("$schema", out var s)
           && s.ValueKind == JsonValueKind.String
           && s.GetString() is { } reference
           && !string.IsNullOrWhiteSpace(reference)
            ? reference
            : null;

    private JsonSchema CompileLocal(string path)
    {
        var key = "file:" + path + ":" + File.GetLastWriteTimeUtc(path).Ticks;
        return _compiled.GetOrAdd(key, _ => JsonSchema.FromText(File.ReadAllText(path)));
    }

    private JsonSchema CompileRemote(string url)
        => _compiled.GetOrAdd("url:" + url, _ => JsonSchema.FromText(FetchSchemaText(url)));

    private static string FetchSchemaText(string url)
    {
        var cacheFile = Path.Combine(CacheDir, Sha1Hex(url) + ".json");
        if (File.Exists(cacheFile))
            return File.ReadAllText(cacheFile);

        var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(cacheFile, text);
        return text;
    }

    private static string Sha1Hex(string s)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}
