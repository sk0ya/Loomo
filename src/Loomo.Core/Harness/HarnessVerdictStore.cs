using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// 人間の判定 <c>.harness/runs/verdicts.json</c>（§37.3）。完了を決めるのはエージェントの自己申告ではなく人間
/// （構想§6）なので、ここが「受け入れ」の唯一の出どころ。ファイルは判定を付けたときに初めて作る。
/// </summary>
public static class HarnessVerdictStore
{
    public static string PathFor(string workspaceFolder)
        => Path.Combine(workspaceFolder, HarnessDefinitionReader.HarnessFolderName, "runs", "verdicts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Dictionary<string, HarnessVerdict> Load(string path)
    {
        var result = new Dictionary<string, HarnessVerdict>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
            if (raw is null)
                return result;
            foreach (var (runId, entry) in raw)
            {
                var kind = entry.Verdict?.ToLowerInvariant() switch
                {
                    "accepted" => HarnessVerdictKind.Accepted,
                    "rejected" => HarnessVerdictKind.Rejected,
                    _ => (HarnessVerdictKind?)null,
                };
                if (kind is { } k)
                    result[runId] = new HarnessVerdict(k, entry.At ?? DateTimeOffset.MinValue, entry.Note);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>
    /// 1件を付ける（<paramref name="verdict"/> が null なら外す）。他の Run の判定はそのまま残す。
    /// <para>ファイルは<b>ここで1件だけ書き換え</b>、読めなかった中身から作り直さない：人間が手で直して壊した
    /// ファイルや、知らない判定（<c>"pending"</c> 等）の行を黙って捨てると、受け入れの唯一の出どころ（構想§6）が消える。
    /// 読めないときは <see cref="InvalidDataException"/> で止め、ファイルには触らない。</para>
    /// </summary>
    public static void Set(string path, string runId, HarnessVerdict? verdict)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path} が JSON として読めないので書き換えない（{ex.Message}）", ex);
            }
            root = parsed as JsonObject
                ?? throw new InvalidDataException($"{path} の中身がオブジェクトではないので書き換えない");
        }
        else
        {
            root = new JsonObject();
        }

        if (verdict is null)
        {
            if (!root.Remove(runId))
                return;
        }
        else
        {
            var entry = new Entry(verdict.Verdict == HarnessVerdictKind.Accepted ? "accepted" : "rejected", verdict.At, verdict.Note);
            root[runId] = JsonSerializer.SerializeToNode(entry, JsonOptions);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Entry(string? Verdict, DateTimeOffset? At, string? Note);
}
