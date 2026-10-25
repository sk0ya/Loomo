using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    IReadOnlyList<int> LinkedWorkItemIds,
    string Author = "",
    string SourceRefName = "",
    string TargetRefName = "",
    DateTimeOffset? CreatedAt = null)
{
    /// <summary>元のブランチ名（<c>refs/heads/</c> を外したもの）。</summary>
    public string SourceBranch => AzureDevOpsPullRequestJson.BranchName(SourceRefName);

    /// <summary>入れ先のブランチ名。</summary>
    public string TargetBranch => AzureDevOpsPullRequestJson.BranchName(TargetRefName);
}

/// <summary>PR を照会する先（TaskAzure の PrTarget と同じ：プロジェクトとリポジトリの組）。</summary>
public sealed record AzureDevOpsPrTarget(string Project, string Repository);

/// <summary>取得の失敗。PAT が受け付けられていない失敗は <see cref="IsAuthentication"/> で見分ける（PAT の取得元を添える）。</summary>
public sealed class AzureDevOpsException(string message, bool isAuthentication = false) : Exception(message)
{
    public bool IsAuthentication { get; } = isAuthentication;
}

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
    private readonly ConcurrentDictionary<(string OrganizationUrl, string Pat), string> _currentUserIds = new();

    public AzureDevOpsWorkItemClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Cookie を持たない。この client はアプリ起動中ずっと1つ（DI のシングルトン）なので、Cookie を溜めると
    /// 古い PAT で失敗したときのサインイン関連の Cookie が、PAT を更新した後の問い合わせにも付いて回る。
    /// PAT は毎回 Authorization ヘッダーで送るので Cookie は要らない（TaskAzure は設定を読むたびに作り直していた）。
    /// </summary>
    internal static HttpClientHandler CreateHandler() => new() { UseCookies = false };

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

    /// <summary>自分がレビュアーに入っている進行中の PR（§24.27.1）。Work Items の一覧には出さない（自分の作業ではない）。
    /// 取れない先は黙って飛ばす（自分の PR と同じ）。</summary>
    public async Task<IReadOnlyList<AzureDevOpsPullRequest>> GetPullRequestsToReviewAsync(
        string organizationUrl, IReadOnlyList<AzureDevOpsPrTarget> targets, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0) return [];
        var userId = await GetCurrentUserIdAsync(organizationUrl, auth, cancellationToken).ConfigureAwait(false);
        var results = await Task.WhenAll(targets.Select(t =>
            FetchPrsForTargetAsync(organizationUrl, t, userId, auth, cancellationToken, "reviewerId"))).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>PR の中身（説明・レビュアーと投票・マージ状態）と会話（スレッド）。読むだけ（§24.27.1）。</summary>
    public async Task<AzureDevOpsPullRequestDetail> GetPullRequestDetailAsync(
        string organizationUrl, AzureDevOpsPrTarget target, int pullRequestId, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken)
    {
        var baseUrl = $"{organizationUrl}/{Uri.EscapeDataString(target.Project)}/_apis/git/repositories/" +
                      $"{Uri.EscapeDataString(target.Repository)}/pullrequests/{pullRequestId}";
        using var pr = await SendAsync(HttpMethod.Get, $"{baseUrl}?$expand=workItemRefs&api-version={ApiVersion}",
            null, auth, cancellationToken).ConfigureAwait(false);
        using var threads = await SendAsync(HttpMethod.Get, $"{baseUrl}/threads?api-version={ApiVersion}",
            null, auth, cancellationToken).ConfigureAwait(false);
        return AzureDevOpsPullRequestJson.ParseDetail(pr.RootElement, threads.RootElement, organizationUrl, target);
    }

    /// <summary>
    /// このリポジトリの最近のビルド（新しい順・コミット一覧の CI の印・§24.93）。読むだけ。ビルドの API はリポジトリを ID で絞るので、
    /// 名前から ID を引いてから問い合わせる（ID は覚えておく）。リポジトリが見つからなければ空。
    /// </summary>
    public async Task<IReadOnlyList<CommitBuild>> GetRecentBuildsAsync(
        string organizationUrl, AzureDevOpsPrTarget target, AuthenticationHeaderValue auth, CancellationToken cancellationToken, int top = 100)
    {
        var project = Uri.EscapeDataString(target.Project);
        var key = (organizationUrl, target.Project, target.Repository);
        if (!_repositoryIds.TryGetValue(key, out var repositoryId))
        {
            using var request = CreateRequest(HttpMethod.Get,
                $"{organizationUrl}/{project}/_apis/git/repositories/{Uri.EscapeDataString(target.Repository)}?api-version={ApiVersion}", null, auth);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];   // 別の組織・リポジトリ名の違い
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var repo = ParseJson(response, text, request.RequestUri?.ToString() ?? "");
            repositoryId = repo.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            if (repositoryId.Length == 0) return [];
            _repositoryIds[key] = repositoryId;
        }
        using var builds = await SendAsync(HttpMethod.Get,
            $"{organizationUrl}/{project}/_apis/build/builds?repositoryId={Uri.EscapeDataString(repositoryId)}&repositoryType=TfsGit" +
            $"&queryOrder=queueTimeDescending&$top={top}&api-version={ApiVersion}", null, auth, cancellationToken).ConfigureAwait(false);
        return AzureDevOpsBuildJson.Parse(builds.RootElement);
    }

    private readonly ConcurrentDictionary<(string, string, string), string> _repositoryIds = new();

    private async Task<IReadOnlyList<AzureDevOpsPullRequest>> FetchPrsForTargetAsync(
        string organizationUrl, AzureDevOpsPrTarget target, string userId, AuthenticationHeaderValue auth,
        CancellationToken cancellationToken, string role = "creatorId")
    {
        var encodedProject = Uri.EscapeDataString(target.Project);
        var encodedRepo = Uri.EscapeDataString(target.Repository);
        var url = $"{organizationUrl}/{encodedProject}/_apis/git/repositories/{encodedRepo}/pullrequests" +
                  $"?searchCriteria.status=active&searchCriteria.{role}={userId}" +
                  $"&$expand=workItemRefs&api-version={ApiVersion}";

        using var request = CreateRequest(HttpMethod.Get, url, null, auth);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];   // リポジトリ名誤りなどは無視

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = ParseJson(response, json, url);
        return ParsePullRequests(doc.RootElement, organizationUrl, target);
    }

    /// <summary>
    /// 自分のユーザー ID（<c>connectionData</c>）。<b>TaskAzure と同じく一度取ったら覚えて使い回す</b>——
    /// TaskAzure は Configure（組織 URL・PAT の設定し直し）まで <c>_currentUserId</c> を持つ。この client は組織 URL と PAT を
    /// 呼ぶたびに受け取るので、その組を鍵にする（どちらかが変われば取り直す＝TaskAzure の Configure に当たる）。
    /// PAT が受け付けられなかったらその PAT の分は捨てる（<see cref="SendAsync"/>）——覚えたままだと、
    /// 接続の確かめ（PAT の候補を順に試す）が覚えた値で素通りし、期限切れの PAT から次の候補へ移れない。
    /// </summary>
    public async Task<string> GetCurrentUserIdAsync(
        string organizationUrl, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        var key = (organizationUrl, auth.Parameter ?? "");
        if (_currentUserIds.TryGetValue(key, out var cached)) return cached;
        using var doc = await SendAsync(HttpMethod.Get, $"{organizationUrl}/_apis/connectionData",
            null, auth, cancellationToken).ConfigureAwait(false);
        var id = doc.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString() ?? "";
        _currentUserIds[key] = id;
        return id;
    }

    private void ForgetCurrentUserIds(AuthenticationHeaderValue auth)
    {
        foreach (var key in _currentUserIds.Keys)
            if (key.Pat == (auth.Parameter ?? ""))
                _currentUserIds.TryRemove(key, out _);
    }

    internal static IReadOnlyList<AzureDevOpsPullRequest> ParsePullRequests(
        JsonElement root, string organizationUrl, AzureDevOpsPrTarget target)
    {
        var result = new List<AzureDevOpsPullRequest>();
        foreach (var item in root.GetProperty("value").EnumerateArray())
            result.Add(AzureDevOpsPullRequestJson.ParsePullRequest(item, organizationUrl, target));
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
        try
        {
            if (!response.IsSuccessStatusCode)
                throw new AzureDevOpsException(
                    $"Azure DevOps が HTTP {(int)response.StatusCode} {response.ReasonPhrase} を返しました: {Shorten(text)}",
                    response.StatusCode is HttpStatusCode.Unauthorized);
            return ParseJson(response, text, url);
        }
        catch (AzureDevOpsException ex) when (ex.IsAuthentication)
        {
            ForgetCurrentUserIds(auth);
            throw;
        }
    }

    /// <summary>
    /// 応答を JSON として読む。読めなければ、何が返ってきたか（状態コード・種類・ページ名・転送先）と
    /// 考えられる原因を文言にして投げる——「JSON ではない」だけでは、PAT・組織 URL・転送のどれが悪いか分からない。
    /// <para>Azure DevOps は PAT を受け付けないとき 401 ではなく <b>203 とサインイン画面の HTML</b> を返すことがある。
    /// 203 は成功扱いの状態コードなので、ここで JSON として読もうとして初めて失敗に気づく。</para>
    /// <para><b>203 でも本文が JSON なら読む</b>（TaskAzure と同じ：EnsureSuccessStatusCode は 203 を通し、本文をそのまま読む）。
    /// 社内のプロキシ・中継は、中身を素通しした正常な応答でも 200 を 203 に書き換えることがある（HTTP の仕様で許されている）。
    /// 203 を本文を見ずにサインイン画面と決めつけていたため、TaskAzure は読めるのに Loomo だけ失敗していた。</para>
    /// </summary>
    internal static JsonDocument ParseJson(HttpResponseMessage response, string text, string requestedUrl)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { }
        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
        var (message, isAuthentication) = DescribeNonJson((int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType, requestedUrl, finalUrl, text);
        throw new AzureDevOpsException(message, isAuthentication);
    }

    internal static (string Message, bool IsAuthentication) DescribeNonJson(
        int statusCode, string? mediaType, string requestedUrl, string? finalUrl, string text)
    {
        var redirected = finalUrl is not null
            && !string.Equals(finalUrl, new Uri(requestedUrl).AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        var title = HtmlTitle(text);
        var signIn = statusCode == 203
            || (finalUrl ?? "").Contains("signin", StringComparison.OrdinalIgnoreCase)
            || (finalUrl ?? "").Contains("login.", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Sign In", StringComparison.OrdinalIgnoreCase)
            || title.Contains("サインイン", StringComparison.Ordinal);

        var cause = signIn
            ? "PAT が受け付けられず、サインイン画面が返ってきました。PAT の期限切れ・別の組織の PAT・PAT に付けた権限の不足（PAT 作成時の Scopes で Work Items と Code に Read が要る）のどれかです。"
            : redirected
                ? "別の場所へ転送されました（転送先には PAT が送られません）。組織 URL を確かめてください。"
                : "組織 URL かプロジェクトが API の場所を指していない可能性があります。";

        var details = new List<string> { $"HTTP {statusCode}" };
        if (!string.IsNullOrEmpty(mediaType)) details.Add(mediaType);
        if (title.Length > 0) details.Add($"ページ「{title}」");
        else if (text.Trim().Length == 0) details.Add("本文なし");
        details.Add(redirected ? $"転送先 {finalUrl}" : $"URL {requestedUrl}");
        return ($"Azure DevOps の応答が JSON ではありませんでした。{cause}（{string.Join(" ・ ", details)}）", signIn);
    }

    private static string HtmlTitle(string text)
    {
        var match = Regex.Match(text, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            ? Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value), @"\s+", " ").Trim()
            : "";
    }

    /// <summary>
    /// 組織 URL を API の根元にそろえる。ブラウザのアドレス欄からそのまま貼ると、
    /// <c>https://dev.azure.com/org/Project/_workitems/…</c> のようにプロジェクトや画面のパスまで付いてきて、
    /// その下に <c>/_apis/…</c> を足しても API に届かない（Web 画面の HTML が返る）。
    /// クラウド（dev.azure.com・*.visualstudio.com）は根元の形が決まっているので切り詰める。
    /// Azure DevOps Server はコレクションまでのパスが環境ごとに違うので触らない。
    /// </summary>
    public static string NormalizeOrganizationUrl(string url)
    {
        var trimmed = (url ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;
        var root = $"{uri.Scheme}://{uri.Authority}";
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var organization = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return organization is null ? root : $"{root}/{organization}";
        }
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return root;
        return trimmed;
    }

    private static string Shorten(string text) => text.Length > 240 ? text[..240] + "…" : text;
}
