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

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
