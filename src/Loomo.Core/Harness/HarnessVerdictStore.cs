using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

    /// <summary>1件を付ける（<paramref name="verdict"/> が null なら外す）。他の Run の判定はそのまま残す。</summary>
    public static void Set(string path, string runId, HarnessVerdict? verdict)
    {
        var all = Load(path);
        if (verdict is null)
        {
            if (!all.Remove(runId))
                return;
        }
        else
        {
            all[runId] = verdict;
        }
        var raw = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (id, v) in all)
            raw[id] = new Entry(v.Verdict == HarnessVerdictKind.Accepted ? "accepted" : "rejected", v.At, v.Note);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(raw, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Entry(string? Verdict, DateTimeOffset? At, string? Note);
}
