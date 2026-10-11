using System.Text.Json;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class AzureDevOpsTests
{
    [Fact]
    public void PATは空ユーザー名のBasicで送る()
    {
        var header = AzureDevOpsPatStore.CreateHeader("patvalue");
        Assert.Equal("Basic", header.Scheme);
        Assert.Equal(":patvalue", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(header.Parameter!)));
    }

    [Fact]
    public void WorkItemの応答を読む_親はrelationsから_取れなかったIDのnullは飛ばす()
    {
        using var doc = JsonDocument.Parse("""
            {"count":2,"value":[
              {"id":12,"fields":{"System.Title":"ログイン","System.WorkItemType":"Task","System.State":"Active"},
               "relations":[{"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://x/_apis/wit/workItems/10"}]},
              null
            ]}
            """);
        var item = Assert.Single(AzureDevOpsWorkItemClient.ParseWorkItems(doc.RootElement.GetProperty("value"),
            "https://tfs.example.net/tfs/Products", "Web App"));
        Assert.Equal(12, item.Id);
        Assert.Equal(10, item.ParentId);
        Assert.Equal("Web App", item.Project);
        Assert.Equal("https://tfs.example.net/tfs/Products/Web%20App/_workitems/edit/12", item.WebUrl);
    }

    [Fact]
    public void 応答が203のサインイン画面なら_PATが受け付けられていないと言う()
    {
        using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NonAuthoritativeInformation)
        {
            Content = new System.Net.Http.StringContent(
                "<html><head><title>Azure DevOps Services | Sign In</title></head></html>",
                System.Text.Encoding.UTF8, "text/html"),
        };
        var ex = Assert.Throws<AzureDevOpsException>(() =>
            AzureDevOpsWorkItemClient.ParseJson(response, "<html><head><title>Azure DevOps Services | Sign In</title></head></html>",
                "https://dev.azure.com/org/_apis/connectionData"));
        Assert.True(ex.IsAuthentication);
        Assert.Contains("PAT が受け付けられず", ex.Message);
        Assert.Contains("HTTP 203", ex.Message);
        Assert.Contains("Sign In", ex.Message);
    }

    [Fact]
    public void 転送されてHTMLが返ったら転送先を示す()
    {
        var (message, isAuth) = AzureDevOpsWorkItemClient.DescribeNonJson(200, "text/html",
            "http://tfs.example.net/tfs/Products/_apis/connectionData",
            "https://tfs.example.net/tfs/Products/_apis/connectionData", "<html><body>x</body></html>");
        Assert.False(isAuth);
        Assert.Contains("転送", message);
        Assert.Contains("https://tfs.example.net/tfs/Products/_apis/connectionData", message);
    }

    [Fact]
    public void 中継が203に書き換えても本文がJSONなら読む_TaskAzureと同じ()
    {
        using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NonAuthoritativeInformation);
        using var doc = AzureDevOpsWorkItemClient.ParseJson(response, """{"workItems":[{"id":1}]}""", "https://dev.azure.com/org/p/_apis/wit/wiql");
        Assert.Equal(1, doc.RootElement.GetProperty("workItems")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void 正しいJSONは読める()
    {
        using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        using var doc = AzureDevOpsWorkItemClient.ParseJson(response, """{"value":[]}""", "https://dev.azure.com/org/_apis/x");
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/Web%20App/_workitems/edit/1", "https://dev.azure.com/org")]
    [InlineData(" https://dev.azure.com/org/ ", "https://dev.azure.com/org")]
    [InlineData("https://org.visualstudio.com/Web App/_boards", "https://org.visualstudio.com")]
    [InlineData("https://tfs.example.net/tfs/Products/", "https://tfs.example.net/tfs/Products")]
    public void 組織URLはAPIの根元にそろえる(string input, string expected)
        => Assert.Equal(expected, AzureDevOpsWorkItemClient.NormalizeOrganizationUrl(input));

    [Fact]
    public void TaskAzureの設定を読む_PAT環境変数名と_PR対象()
    {
        var settings = TaskAzureSettings.Parse("""
            {"OrganizationUrl":"https://tfs.example.net/tfs/Products/","Project":"Web App","PatEnvVarName":"MY_PAT",
             "RefreshIntervalMinutes":5,"PrTargets":[{"Project":"Web App","Repository":"web"},{"Project":"","Repository":"x"}]}
            """);
        Assert.NotNull(settings);
        Assert.Equal("https://tfs.example.net/tfs/Products", settings.OrganizationUrl);
        Assert.Equal("Web App", settings.Project);
        Assert.Equal("MY_PAT", settings.PatEnvVarName);
        Assert.Equal([new AzureDevOpsPrTarget("Web App", "web")], settings.PrTargets);
    }

    [Fact]
    public void TaskAzureの設定でPAT環境変数名が空ならADO_PAT_組織URLが空なら無いのと同じ()
    {
        Assert.Equal("ADO_PAT", TaskAzureSettings.Parse("""{"OrganizationUrl":"https://dev.azure.com/o","PatEnvVarName":""}""")!.PatEnvVarName);
        Assert.Null(TaskAzureSettings.Parse("""{"OrganizationUrl":""}"""));
    }

    [Fact]
    public void 起動中ずっと使うHTTPクライアントはCookieを溜めない()
    {
        using var handler = AzureDevOpsWorkItemClient.CreateHandler();
        Assert.False(handler.UseCookies);
    }

    private static AzureDevOpsWorkItem Item(int id, int parent = 0, string type = "Task")
        => new(id, $"item {id}", type, "Active", "P", "P\\Sprint", parent, null, $"u/{id}");

    [Fact]
    public void Storyの下にTaskを並べ_自分の担当でない親は文脈として出す()
    {
        // 更新の新しい順：Task 3 → Bug 5 → Task 4。3 と 4 の親 Story 1 は自分の担当ではない。
        var assigned = new[] { Item(3, parent: 1), Item(5, type: "Bug"), Item(4, parent: 1) };
        var parents = new[] { Item(1, type: "User Story") };

        var rows = WorkItemTree.Arrange(assigned, parents);

        Assert.Equal([1, 3, 4, 5], rows.Select(r => r.Item.Id));
        Assert.Equal([0, 1, 1, 0], rows.Select(r => r.Depth));
        Assert.False(rows[0].IsAssigned);
        Assert.True(rows[1].IsAssigned);
    }

    [Fact]
    public void 親子リンクが循環していても全件を出す()
    {
        var rows = WorkItemTree.Arrange([Item(1, parent: 2), Item(2, parent: 1)], []);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void PRの一覧とworkItemRefsを読む()
    {
        using var list = JsonDocument.Parse("""
            {"value":[{"pullRequestId":7,"title":"ログイン修正","isDraft":true,
              "workItemRefs":[{"id":"12","url":"u"},{"id":"13","url":"u"}]}]}
            """);
        var pull = Assert.Single(AzureDevOpsWorkItemClient.ParsePullRequests(
            list.RootElement, "https://dev.azure.com/contoso", new AzureDevOpsPrTarget("Web", "site")));
        Assert.True(pull.IsDraft);
        Assert.Equal([12, 13], pull.LinkedWorkItemIds);
        Assert.Equal("https://dev.azure.com/contoso/Web/_git/site/pullrequest/7", pull.WebUrl);
    }

    private static AzureDevOpsPullRequest Pull(int id, params int[] linked)
        => new(id, $"pr {id}", "P", "repo", false, $"p/{id}", linked);

    [Fact]
    public void PRは紐づくWorkItemの下に_紐づかないPRは末尾の見出しの下に並ぶ()
    {
        var rows = WorkItemTree.Arrange([Item(3, parent: 1), Item(5, type: "Bug")], [Item(1, type: "User Story")]);
        var entries = WorkItemList.Build(rows, [Pull(70, 3), Pull(71), Pull(72, 999)]);

        Assert.Equal(
            ["W1", "W3", "P70", "W5", "S", "P71", "P72"],
            entries.Select(e => e.Kind switch
            {
                WorkItemListEntryKind.WorkItem => $"W{e.WorkItem!.Id}",
                WorkItemListEntryKind.PullRequest => $"P{e.PullRequest!.Id}",
                _ => "S",
            }));
        Assert.Equal(1, entries[2].ParentIndex);   // PR 70 → Task 3
        Assert.Equal(0, entries[1].ParentIndex);   // Task 3 → Story 1
    }

    [Fact]
    public void 絞り込み_子が合えば親は道しるべとして残り_合ったWorkItemのPRは一緒に残る()
    {
        var rows = WorkItemTree.Arrange(
            [Item(3, parent: 1) with { Title = "ログイン画面" }, Item(4, parent: 1) with { Title = "一覧" }],
            [Item(1, type: "User Story") with { Title = "認証" }]);
        var entries = WorkItemList.Build(rows, [Pull(70, 3), Pull(71)]);

        var visible = WorkItemList.Visible(entries, new WorkItemListFilter("ログイン"));
        Assert.Equal(["W1", "W3", "P70"], Shown(entries, visible));

        // 状態で絞るとき、紐づかない PR（状態を持たない）は出さない。
        visible = WorkItemList.Visible(entries, new WorkItemListFilter("", ["Active"]));
        Assert.Equal(["W1", "W3", "P70", "W4"], Shown(entries, visible));

        // 文字が PR に合えば、PR だけでも残る（見出しは道しるべ）。
        visible = WorkItemList.Visible(entries, new WorkItemListFilter("pr 71"));
        Assert.Equal(["S", "P71"], Shown(entries, visible));
    }

    [Fact]
    public void 絞り込み_状態は複数選べて_どれかに合えば残る()
    {
        var rows = WorkItemTree.Arrange(
            [Item(3) with { State = "Active" }, Item(4) with { State = "New" }, Item(5) with { State = "Resolved" }], []);
        var entries = WorkItemList.Build(rows, []);

        var visible = WorkItemList.Visible(entries, new WorkItemListFilter("", ["Active", "New"]));
        Assert.Equal(["W3", "W4"], Shown(entries, visible));

        // 空集合は「絞らない」。
        visible = WorkItemList.Visible(entries, new WorkItemListFilter("", [], []));
        Assert.Equal(["W3", "W4", "W5"], Shown(entries, visible));
    }

    [Fact]
    public void 折りたたみ_畳んだ行は残り子孫だけ隠れる()
    {
        var rows = WorkItemTree.Arrange([Item(3, parent: 1), Item(5, type: "Bug")], [Item(1, type: "User Story")]);
        var entries = WorkItemList.Build(rows, [Pull(70, 3), Pull(71)]);
        var all = WorkItemList.Visible(entries, new WorkItemListFilter(""));

        var hasChildren = WorkItemList.HasVisibleChildren(entries, all);
        Assert.Equal(["W1", "W3", "S"], Shown(entries, hasChildren));

        // Story 1 を畳むと、孫の PR 70 まで隠れる。
        var story = WorkItemList.CollapseKey(entries[0]);
        var shown = WorkItemList.Collapse(entries, all, i => WorkItemList.CollapseKey(entries[i]) == story);
        Assert.Equal(["W1", "W5", "S", "P71"], Shown(entries, shown));

        // PR は子を持てないので鍵が無い。
        Assert.Null(WorkItemList.CollapseKey(entries[2]));
    }

    private static string[] Shown(IReadOnlyList<WorkItemListEntry> entries, bool[] visible)
        => entries.Where((_, i) => visible[i]).Select(e => e.Kind switch
        {
            WorkItemListEntryKind.WorkItem => $"W{e.WorkItem!.Id}",
            WorkItemListEntryKind.PullRequest => $"P{e.PullRequest!.Id}",
            _ => "S",
        }).ToArray();
}
