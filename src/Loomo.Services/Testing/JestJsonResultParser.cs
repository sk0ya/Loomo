using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>jest 互換 JSON（<c>jest --json</c> / <c>vitest --reporter=json</c>）のパース。両者が同じ形
/// （<c>testResults[].assertionResults[]</c>）を出すので 1 本で足りる（実 vitest で形状検証済み・§29.4）。
/// <c>ancestorTitles</c> + <c>title</c> を「a &gt; b &gt; タイトル」に組み立てる——探索側（<c>TsTestDiscovery</c>）と同じ形。</summary>
public static class JestJsonResultParser
{
    public static IReadOnlyList<ScriptTestResult> Parse(string json)
    {
        var list = new List<ScriptTestResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("testResults", out var files) || files.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var file in files.EnumerateArray())
        {
            var path = file.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!file.TryGetProperty("assertionResults", out var asserts) || asserts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var a in asserts.EnumerateArray())
            {
                var title = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (a.TryGetProperty("ancestorTitles", out var anc) && anc.ValueKind == JsonValueKind.Array)
                {
                    var ancestors = anc.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                    if (ancestors.Count > 0) title = string.Join(" > ", ancestors) + " > " + title;
                }

                var status = a.TryGetProperty("status", out var st) ? st.GetString() : null;
                var outcome = status switch
                {
                    "passed" => ScriptTestOutcome.Passed,
                    "failed" => ScriptTestOutcome.Failed,
                    _ => ScriptTestOutcome.Skipped,   // pending / skipped / todo / disabled
                };

                string? message = null;
                if (outcome == ScriptTestOutcome.Failed &&
                    a.TryGetProperty("failureMessages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    var first = msgs.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        // 失敗メッセージは複数行（スタック付き）——1 行目だけを一覧の 2 行目に出す。
                        var text = first.GetString() ?? "";
                        var nl = text.IndexOf('\n');
                        message = (nl >= 0 ? text[..nl] : text).Trim();
                    }
                }

                // duration はミリ秒（vitest/jest とも数値。skip したテストでは欠けることがある）。
                TimeSpan? duration = a.TryGetProperty("duration", out var dur)
                    && dur.ValueKind == JsonValueKind.Number && dur.TryGetDouble(out var ms) && ms >= 0
                        ? TimeSpan.FromMilliseconds(ms) : null;

                list.Add(new ScriptTestResult(path, title, outcome, message, duration));
            }
        }
        return list;
    }
}
