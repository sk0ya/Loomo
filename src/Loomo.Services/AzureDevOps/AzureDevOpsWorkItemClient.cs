using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>プルリクエスト1件ぶんの表示に要る値。</summary>
public sealed record AzureDevOpsPullRequest(
    int Id,
    string Title,
    string Project,
    string Repository,
    bool IsDraft,
    string WebUrl,
    IReadOnlyList<int> LinkedWorkItemIds);

/// <summary>PR を照会する先（TaskAzure の PrTarget と同じ：プロジェクトとリポジトリの組）。</summary>
public sealed record AzureDevOpsPrTarget(string Project, string Repository);

/// <summary>取得の失敗。</summary>
public sealed class AzureDevOpsException(string message) : Exception(message);

/// <summary>
/// Azure DevOps の REST。<b>TaskAzure の <c>AzureDevOpsService</c> と同じリクエストを送る</b>——
/// 組織 URL とプロジェクトは設定から、WIQL はプロジェクト単位、詳細は <c>workitems?ids=…&amp;$expand=relations</c>、
/// PR は設定したリポジトリごとに <c>$expand=workItemRefs</c>。TaskAzure は職場の環境（Azure DevOps Server を含む）で
/// 実際に動いているので、問い合わせの形はそこから変えない。
/// </summary>
public sealed class AzureDevOpsWorkItemClient
{
    private const string ApiVersion = "7.1";
    private const int BatchLimit = 200;

    private readonly HttpClient _http;

    public AzureDevOpsWorkItemClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>自分に割り当たっている未完了の Work Item（更新の新しい順、最大 200 件）。</summary>
    public async Task<IReadOnlyList<AzureDevOpsWorkItem>> GetMyWorkItemsAsync(
        string organizationUrl, string project, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        var wiqlBody = JsonSerializer.Serialize(new
        {
            query = "SELECT [System.Id] FROM workitems WHERE [System.AssignedTo] = @Me " +
                    "AND [System.State] NOT IN ('Closed','Done','Removed') " +
                    "ORDER BY [System.ChangedDate] DESC"
        });
        var wiqlUrl = $"{organizationUrl}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?api-version={ApiVersion}";
        using var wiql = await SendAsync(HttpMethod.Post, wiqlUrl, wiqlBody, auth, cancellationToken).ConfigureAwait(false);

        var ids = new List<int>();
        foreach (var item in wiql.RootElement.GetProperty("workItems").EnumerateArray())
        {
            ids.Add(item.GetProperty("id").GetInt32());
            if (ids.Count >= BatchLimit) break;
        }
        if (ids.Count == 0) return [];

        // $expand=relations で親子リンク(Hierarchy)も取得する
        var url = $"{organizationUrl}/_apis/wit/workitems?ids={string.Join(",", ids)}&$expand=relations&api-version={ApiVersion}";
        using var doc = await SendAsync(HttpMethod.Get, url, null, auth, cancellationToken).ConfigureAwait(false);
        return ParseWorkItems(doc.RootElement.GetProperty("value"), organizationUrl, project);
    }

    /// <summary>ID 指定で Work Item を取得する（削除済み ID は無視）。自分の Task の親を見出しに出すのに使う。</summary>
    public async Task<IReadOnlyList<AzureDevOpsWorkItem>> GetWorkItemsByIdsAsync(
        string organizationUrl, string project, IReadOnlyList<int> ids, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken)
    {
        var result = new List<AzureDevOpsWorkItem>();
        for (var i = 0; i < ids.Count; i += BatchLimit)
        {
            var chunk = ids.Skip(i).Take(BatchLimit).ToList();
            // $expand=relations で親子リンク(Hierarchy)も取得する
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ids"] = chunk,
                ["$expand"] = "relations",
                ["errorPolicy"] = "omit",
            });
            using var doc = await SendAsync(HttpMethod.Post,
                $"{organizationUrl}/_apis/wit/workitemsbatch?api-version={ApiVersion}",
                body, auth, cancellationToken).ConfigureAwait(false);
            result.AddRange(ParseWorkItems(doc.RootElement.GetProperty("value"), organizationUrl, project));
        }
        return result;
    }

    /// <summary>自分が作成した進行中の PR（設定したリポジトリごと）。リポジトリ名誤りなどで取れない先は黙って飛ばす。</summary>
    public async Task<IReadOnlyList<AzureDevOpsPullRequest>> GetMyPullRequestsAsync(
        string organizationUrl, IReadOnlyList<AzureDevOpsPrTarget> targets, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0) return [];
        var userId = await GetCurrentUserIdAsync(organizationUrl, auth, cancellationToken).ConfigureAwait(false);
        var results = await Task.WhenAll(targets.Select(t =>
            FetchPrsForTargetAsync(organizationUrl, t, userId, auth, cancellationToken))).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    private async Task<IReadOnlyList<AzureDevOpsPullRequest>> FetchPrsForTargetAsync(
        string organizationUrl, AzureDevOpsPrTarget target, string userId, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken)
    {
        var encodedProject = Uri.EscapeDataString(target.Project);
        var encodedRepo = Uri.EscapeDataString(target.Repository);
        var url = $"{organizationUrl}/{encodedProject}/_apis/git/repositories/{encodedRepo}/pullrequests" +
                  $"?searchCriteria.status=active&searchCriteria.creatorId={userId}" +
                  $"&$expand=workItemRefs&api-version={ApiVersion}";

        using var request = CreateRequest(HttpMethod.Get, url, null, auth);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];   // リポジトリ名誤りなどは無視

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return ParsePullRequests(doc.RootElement, organizationUrl, target);
    }

    private async Task<string> GetCurrentUserIdAsync(
        string organizationUrl, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{organizationUrl}/_apis/connectionData",
            null, auth, cancellationToken).ConfigureAwait(false);
        return doc.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString() ?? "";
    }

    internal static IReadOnlyList<AzureDevOpsPullRequest> ParsePullRequests(
        JsonElement root, string organizationUrl, AzureDevOpsPrTarget target)
    {
        var encodedProject = Uri.EscapeDataString(target.Project);
        var encodedRepo = Uri.EscapeDataString(target.Repository);
        var result = new List<AzureDevOpsPullRequest>();
        foreach (var item in root.GetProperty("value").EnumerateArray())
        {
            var prId = item.GetProperty("pullRequestId").GetInt32();
            var linkedIds = new List<int>();
            if (item.TryGetProperty("workItemRefs", out var refs) && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in refs.EnumerateArray())
                {
                    if (r.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String
                        && int.TryParse(idProp.GetString(), out var wid))
                        linkedIds.Add(wid);
                }
            }
            result.Add(new AzureDevOpsPullRequest(
                prId,
                TryGetString(item, "title"),
                target.Project,
                target.Repository,
                item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                $"{organizationUrl}/{encodedProject}/_git/{encodedRepo}/pullrequest/{prId}",
                linkedIds));
        }
        return result;
    }

    internal static IReadOnlyList<AzureDevOpsWorkItem> ParseWorkItems(
        JsonElement valueArray, string organizationUrl, string project)
    {
        var result = new List<AzureDevOpsWorkItem>();
        foreach (var item in valueArray.EnumerateArray())
        {
            // errorPolicy=omit では、取れなかった ID が null で並ぶ。
            if (item.ValueKind != JsonValueKind.Object) continue;
            var itemId = item.GetProperty("id").GetInt32();
            var fields = item.GetProperty("fields");
            var itemProject = GetFieldText(fields, "System.TeamProject");
            result.Add(new AzureDevOpsWorkItem(
                itemId,
                GetFieldText(fields, "System.Title"),
                GetFieldText(fields, "System.WorkItemType"),
                GetFieldText(fields, "System.State"),
                itemProject.Length > 0 ? itemProject : project,
                GetFieldText(fields, "System.IterationPath"),
                ExtractParentId(item, fields),
                fields.TryGetProperty("System.ChangedDate", out var changed) && changed.TryGetDateTimeOffset(out var at)
                    ? at : null,
                $"{organizationUrl}/{Uri.EscapeDataString(project)}/_workitems/edit/{itemId}"));
        }
        return result;
    }

    /// <summary>relations の Hierarchy-Reverse から親 WorkItem ID を取得。無ければ System.Parent フィールド</summary>
    private static int ExtractParentId(JsonElement item, JsonElement fields)
    {
        if (item.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rels.EnumerateArray())
            {
                if (r.TryGetProperty("rel", out var rel)
                    && rel.GetString() == "System.LinkTypes.Hierarchy-Reverse"
                    && r.TryGetProperty("url", out var urlProp))
                {
                    var url = urlProp.GetString() ?? "";
                    var slash = url.LastIndexOf('/');
                    if (slash >= 0 && slash < url.Length - 1
                        && int.TryParse(url[(slash + 1)..], out var pid))
                        return pid;
                }
            }
        }

        if (fields.TryGetProperty("System.Parent", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n))
            return n;
        return 0;
    }

    private static string GetFieldText(JsonElement fields, string fieldName)
    {
        if (!fields.TryGetProperty(fieldName, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                return dn.GetString() ?? "";
            if (value.TryGetProperty("uniqueName", out var un) && un.ValueKind == JsonValueKind.String)
                return un.GetString() ?? "";
        }
        return value.ToString();
    }

    private static string TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? body, AuthenticationHeaderValue auth)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Authorization = auth;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>TaskAzure の EnsureSuccessStatusCode と同じく失敗は例外にする。文言だけ読める形にする。</summary>
    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? body,
        AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, url, body, auth);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AzureDevOpsException(
                $"Azure DevOps が HTTP {(int)response.StatusCode} {response.ReasonPhrase} を返しました: {Shorten(text)}");
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // サインイン画面の HTML などが返ってきた（PAT が通っていない）。
            throw new AzureDevOpsException($"Azure DevOps の応答が JSON ではありませんでした（HTTP {(int)response.StatusCode}）。"
                + "PAT と組織 URL を確かめてください。");
        }
    }

    private static string Shorten(string text) => text.Length > 240 ? text[..240] + "…" : text;
}
