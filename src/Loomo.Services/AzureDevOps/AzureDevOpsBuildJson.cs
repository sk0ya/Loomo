using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Services;

/// <summary>Azure Pipelines の <c>_apis/build/builds</c> の応答を読む（純粋・§24.93）。</summary>
public static class AzureDevOpsBuildJson
{
    public static IReadOnlyList<CommitBuild> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().Select(build =>
        {
            var state = Str(build, "status") != "completed"
                ? CommitBuildState.Pending   // notStarted / inProgress / postponed / cancelling
                : Str(build, "result") switch
                {
                    "succeeded" => CommitBuildState.Success,
                    "" or "none" => CommitBuildState.Pending,
                    _ => CommitBuildState.Failure,   // failed / partiallySucceeded / canceled
                };
            var name = build.TryGetProperty("definition", out var definition) ? Str(definition, "name") : "";
            string? url = null;
            if (build.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var web))
                url = Str(web, "href") is { Length: > 0 } href ? href : null;
            return new CommitBuild(Str(build, "sourceVersion"), name.Length > 0 ? name : "ビルド", state, url);
        }).Where(b => b.Sha.Length > 0).ToList();
    }

    /// <summary>
    /// パイプラインの実行の段（<c>_apis/build/builds/{id}/timeline</c> の records）のうち、<b>失敗したもの</b>と
    /// エラー・警告の issue を持つもの（ブラウザサポート §41）。issue の場所は <c>data.sourcepath</c>／<c>linenumber</c>
    /// にあればそれ、無ければ本文（<c>path(12,5): error …</c>）から読む側（<see cref="CiLogProblems"/>）が拾う。
    /// </summary>
    public static IReadOnlyList<AzureDevOpsBuildStep> ParseTimeline(JsonElement root)
    {
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return [];
        var steps = new List<AzureDevOpsBuildStep>();
        foreach (var record in records.EnumerateArray())
        {
            var issues = new List<AzureDevOpsBuildIssue>();
            if (record.TryGetProperty("issues", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var issue in list.EnumerateArray())
                {
                    var type = Str(issue, "type");
                    if (type is not ("error" or "warning")) continue;
                    var data = issue.TryGetProperty("data", out var d) ? d : default;
                    issues.Add(new AzureDevOpsBuildIssue(type, Str(issue, "message"),
                        Str(data, "sourcepath") is { Length: > 0 } path ? path : null,
                        Int(Str(data, "linenumber")), Int(Str(data, "columnnumber"))));
                }
            var result = Str(record, "result");
            if (result != "failed" && issues.Count == 0) continue;
            var logId = record.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object
                && log.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0;
            steps.Add(new AzureDevOpsBuildStep(Str(record, "name"), Str(record, "type"), result, logId, issues,
                record.TryGetProperty("order", out var order) && order.TryGetInt32(out var o) ? o : 0));
        }
        return steps;
    }

    private static int Int(string text) => int.TryParse(text, out var n) && n > 0 ? n : 0;

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}

/// <summary>パイプラインの1段（Stage／Job／Task）の失敗・issue（§41）。</summary>
public sealed record AzureDevOpsBuildStep(string Name, string RecordType, string Result, int LogId,
    IReadOnlyList<AzureDevOpsBuildIssue> Issues, int Order = 0)
{
    public bool Failed => Result == "failed";
}

/// <summary>段に付いたエラー・警告。<paramref name="SourcePath"/> はエージェントの上のパス（無いことが多い）。</summary>
public sealed record AzureDevOpsBuildIssue(string Type, string Message, string? SourcePath, int Line, int Column);
