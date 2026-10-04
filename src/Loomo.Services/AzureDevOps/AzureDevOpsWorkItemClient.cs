using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>Work Item 1件ぶんの表示に要る値。</summary>
public sealed record AzureDevOpsWorkItem(
    int Id,
    string Title,
    string WorkItemType,
    string State,
    string Project,
    string IterationPath,
    int ParentId,
    DateTimeOffset? ChangedDate,
    string WebUrl);

/// <summary>取得の失敗。認証の失敗は <see cref="IsAuthentication"/> で見分ける（案内の文言が違う）。</summary>
public sealed class AzureDevOpsException(string message, bool isAuthentication = false) : Exception(message)
{
    public bool IsAuthentication { get; } = isAuthentication;
}

/// <summary>
/// Azure DevOps の Work Items REST（WIQL → workitemsbatch）。組織単位で照会するので、プロジェクトの指定は要らない
/// ——「自分に割り当たっているもの」はプロジェクトをまたいで1つの一覧で見たい。
/// </summary>
public sealed class AzureDevOpsWorkItemClient
{
    private const string ApiVersion = "7.1";
    private const int BatchLimit = 200;

    /// <summary>終わっていない状態だけ。プロセステンプレートごとに完了の名前が違うので、代表的なものを並べる。</summary>
    private const string AssignedToMeQuery =
        "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me " +
        "AND [System.State] NOT IN ('Closed','Done','Removed','Resolved','Completed') " +
        "ORDER BY [System.ChangedDate] DESC";

    private static readonly string[] Fields =
    [
        "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.TeamProject",
        "System.IterationPath", "System.Parent", "System.ChangedDate",
    ];

    private readonly HttpClient _http;

    public AzureDevOpsWorkItemClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>自分に割り当たっている未完了の Work Item（更新の新しい順、最大 200 件）。</summary>
    public async Task<IReadOnlyList<AzureDevOpsWorkItem>> GetAssignedToMeAsync(
        AzureDevOpsOrganization organization, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { query = AssignedToMeQuery });
        using var wiql = await SendAsync(HttpMethod.Post,
            $"{organization.BaseUrl}/_apis/wit/wiql?$top={BatchLimit}&api-version={ApiVersion}",
            body, auth, cancellationToken).ConfigureAwait(false);

        var ids = new List<int>();
        if (wiql.RootElement.TryGetProperty("workItems", out var refs) && refs.ValueKind == JsonValueKind.Array)
            foreach (var r in refs.EnumerateArray())
                if (r.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
                    ids.Add(value);
        return await GetByIdsAsync(organization, ids, auth, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ID 指定で取る（削除済み・見えないものは黙って抜ける）。並びは <paramref name="ids"/> の順。</summary>
    public async Task<IReadOnlyList<AzureDevOpsWorkItem>> GetByIdsAsync(
        AzureDevOpsOrganization organization, IReadOnlyList<int> ids, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<int, AzureDevOpsWorkItem>();
        foreach (var chunk in ids.Distinct().Chunk(BatchLimit))
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ids"] = chunk,
                ["fields"] = Fields,
                ["errorPolicy"] = "omit",
            });
            using var doc = await SendAsync(HttpMethod.Post,
                $"{organization.BaseUrl}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
                body, auth, cancellationToken).ConfigureAwait(false);
            foreach (var item in ParseBatch(doc.RootElement, organization))
                byId[item.Id] = item;
        }
        return ids.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    internal static IEnumerable<AzureDevOpsWorkItem> ParseBatch(JsonElement root, AzureDevOpsOrganization organization)
    {
        if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in values.EnumerateArray())
        {
            // errorPolicy=omit では、取れなかった ID が null で並ぶ。
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idProp)
                || !idProp.TryGetInt32(out var id) || !item.TryGetProperty("fields", out var fields))
                continue;
            var project = Text(fields, "System.TeamProject");
            yield return new AzureDevOpsWorkItem(
                id,
                Text(fields, "System.Title"),
                Text(fields, "System.WorkItemType"),
                Text(fields, "System.State"),
                project,
                Text(fields, "System.IterationPath"),
                fields.TryGetProperty("System.Parent", out var parent) && parent.TryGetInt32(out var parentId) ? parentId : 0,
                fields.TryGetProperty("System.ChangedDate", out var changed) && changed.TryGetDateTimeOffset(out var at) ? at : null,
                project.Length > 0
                    ? $"{organization.BaseUrl}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}"
                    : $"{organization.BaseUrl}/_workitems/edit/{id}");
        }
    }

    private static string Text(JsonElement fields, string name)
        => fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string body,
        AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = auth;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // 認証が通らないとき Azure DevOps は 401 だけでなく、203 でサインイン画面の HTML を返すことがある。
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation
            || (response.IsSuccessStatusCode && !LooksLikeJson(text)))
            throw new AzureDevOpsException("Azure DevOps の認証が通りませんでした。", isAuthentication: true);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new AzureDevOpsException("この組織の Work Items を読む権限がありません。", isAuthentication: true);
        if (!response.IsSuccessStatusCode)
            throw new AzureDevOpsException($"Azure DevOps が HTTP {(int)response.StatusCode} を返しました: {ErrorMessage(text)}");
        return JsonDocument.Parse(text);
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[';
    }

    private static string ErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "";
        }
        catch (JsonException) { }
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}
