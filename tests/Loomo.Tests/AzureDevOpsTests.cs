using System.Text.Json;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class AzureDevOpsTests
{
    [Theory]
    [InlineData("https://dev.azure.com/contoso/Web/_git/site", "contoso", "dev.azure.com", "contoso")]
    [InlineData("https://contoso@dev.azure.com/contoso/Web/_git/site", "contoso", "dev.azure.com", "contoso")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Web/site", "contoso", "dev.azure.com", "contoso")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/contoso/Web/site", "contoso", "dev.azure.com", "contoso")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Web/_git/site", "contoso", "contoso.visualstudio.com", "")]
    [InlineData("contoso@vs-ssh.visualstudio.com:v3/contoso/Web/site", "contoso", "contoso.visualstudio.com", "")]
    public void リモートURLから組織を読む(string remote, string name, string host, string path)
    {
        Assert.True(AzureDevOpsOrganization.TryParseRemote(remote, out var org));
        Assert.Equal(name, org.Name);
        Assert.Equal(host, org.CredentialHost);
        Assert.Equal(path, org.CredentialPath);
    }

    [Theory]
    [InlineData("https://github.com/sk0ya/Loomo.git")]
    [InlineData("git@github.com:sk0ya/Loomo.git")]
    [InlineData("https://dev.azure.com/")]
    [InlineData("")]
    public void AzureDevOps以外のリモートは読まない(string remote)
        => Assert.False(AzureDevOpsOrganization.TryParseRemote(remote, out _));

    [Theory]
    [InlineData("contoso", "https://dev.azure.com/contoso")]
    [InlineData("https://dev.azure.com/contoso/", "https://dev.azure.com/contoso")]
    [InlineData("dev.azure.com/contoso", "https://dev.azure.com/contoso")]
    [InlineData("https://contoso.visualstudio.com", "https://contoso.visualstudio.com")]
    public void 設定の入力から組織を読む(string input, string baseUrl)
    {
        Assert.True(AzureDevOpsOrganization.TryParseUserInput(input, out var org));
        Assert.Equal(baseUrl, org.BaseUrl);
    }

    [Fact]
    public void Entraのトークンは_Bearer_PATは_Basic()
    {
        Assert.Equal("Bearer", GitCredentialTokenProvider.CreateHeader("eyJhbGciOi.eyJzdWIi.c2ln").Scheme);
        var basic = GitCredentialTokenProvider.CreateHeader("patvalue");
        Assert.Equal("Basic", basic.Scheme);
        Assert.Equal(":patvalue", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(basic.Parameter!)));
    }

    [Fact]
    public void credential_fill_の出力から値を取る()
    {
        var output = "protocol=https\r\nhost=dev.azure.com\r\nusername=me\r\npassword=a=b\r\n";
        Assert.Equal("a=b", GitCredentialTokenProvider.ParseValue(output, "password"));
        Assert.Null(GitCredentialTokenProvider.ParseValue(output, "pass"));
    }

    [Fact]
    public void workitemsbatch_の応答を読む_取れなかったIDのnullは飛ばす()
    {
        using var doc = JsonDocument.Parse("""
            {"count":2,"value":[
              {"id":12,"fields":{"System.Title":"ログイン","System.WorkItemType":"Task","System.State":"Active",
                "System.TeamProject":"Web App","System.Parent":10,"System.ChangedDate":"2026-10-01T00:00:00Z"}},
              null
            ]}
            """);
        var item = Assert.Single(AzureDevOpsWorkItemClient.ParseBatch(doc.RootElement,
            AzureDevOpsOrganization.FromName("contoso")));
        Assert.Equal(12, item.Id);
        Assert.Equal(10, item.ParentId);
        Assert.Equal("https://dev.azure.com/contoso/Web%20App/_workitems/edit/12", item.WebUrl);
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
}
