using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ブラウザサポート（§41）：Azure Repos の URL の読み方・ページごとの道具・手元のクローンが要る道具の出し分け（純粋）。</summary>
public class BrowserSupportTests
{
    [Fact]
    public void PullRequest_OnDevAzureCom()
    {
        var page = AzureDevOpsWebUrl.TryParse("https://dev.azure.com/org/Proj/_git/Repo/pullrequest/123?_a=files&path=/src/a.cs");
        Assert.NotNull(page);
        Assert.Equal(AzureDevOpsWebPageKind.PullRequest, page.Kind);
        Assert.Equal("https://dev.azure.com/org", page.OrganizationUrl);
        Assert.Equal("Proj", page.Project);
        Assert.Equal("Repo", page.Repository);
        Assert.Equal(123, page.PullRequestId);
    }

    [Fact]
    public void PullRequest_OnVisualStudioCom_WithAndWithoutDefaultCollection()
    {
        var plain = AzureDevOpsWebUrl.TryParse("https://org.visualstudio.com/Proj/_git/Repo/pullrequest/7");
        Assert.Equal(("https://org.visualstudio.com", "Proj", "Repo", 7),
            (plain!.OrganizationUrl, plain.Project, plain.Repository, plain.PullRequestId));
        var collection = AzureDevOpsWebUrl.TryParse("https://org.visualstudio.com/DefaultCollection/Proj/_git/Repo/pullrequest/7");
        Assert.Equal(("https://org.visualstudio.com/DefaultCollection", "Proj"), (collection!.OrganizationUrl, collection.Project));
    }

    [Fact]
    public void Server_TfsCollection()
    {
        var page = AzureDevOpsWebUrl.TryParse("https://tfs.example.com/tfs/DefaultCollection/Proj/_git/Repo/commit/abcdef1234567");
        Assert.Equal(AzureDevOpsWebPageKind.Commit, page!.Kind);
        Assert.Equal("https://tfs.example.com/tfs/DefaultCollection", page.OrganizationUrl);
        Assert.Equal("abcdef1234567", page.CommitHash);
    }

    [Fact]
    public void ProjectOmitted_RepositoryNameIsProject()
    {
        var page = AzureDevOpsWebUrl.TryParse("https://dev.azure.com/org/_git/Tools/pullrequest/5");
        Assert.Equal(("Tools", "Tools"), (page!.Project, page.Repository));
    }

    [Fact]
    public void EscapedNames_AreDecoded()
    {
        var page = AzureDevOpsWebUrl.TryParse("https://dev.azure.com/org/My%20Project/_git/My%20Repo/pullrequest/1");
        Assert.Equal(("My Project", "My Repo"), (page!.Project, page.Repository));
        Assert.Equal("https://dev.azure.com/org", page.OrganizationUrl);
    }

    [Fact]
    public void File_WithLineAndBranch()
    {
        var page = AzureDevOpsWebUrl.TryParse(
            "https://dev.azure.com/org/Proj/_git/Repo?path=%2Fsrc%2FApp%2FMain.cs&version=GBfeature%2Fx&line=42&lineEnd=43&lineStartColumn=1&_a=contents");
        Assert.Equal(AzureDevOpsWebPageKind.File, page!.Kind);
        Assert.Equal("/src/App/Main.cs", page.FilePath);
        Assert.Equal(42, page.Line);
        Assert.Equal("feature/x", page.VersionBranch);
    }

    [Fact]
    public void RepositoryTop_IsNotAFile()
    {
        Assert.Equal(AzureDevOpsWebPageKind.Repository, AzureDevOpsWebUrl.TryParse("https://dev.azure.com/org/Proj/_git/Repo?path=/")!.Kind);
        Assert.Equal(AzureDevOpsWebPageKind.Repository, AzureDevOpsWebUrl.TryParse("https://dev.azure.com/org/Proj/_git/Repo/pullrequests")!.Kind);
    }

    [Theory]
    [InlineData("https://github.com/org/repo/pull/1")]
    [InlineData("https://dev.azure.com/org/Proj/_workitems/edit/1")]
    [InlineData("https://example.com/a/_git/b")]   // Azure DevOps のホストではない
    [InlineData("file:///C:/a/_git/b")]
    [InlineData("")]
    [InlineData(null)]
    public void NotAzureRepos(string? url) => Assert.Null(AzureDevOpsWebUrl.TryParse(url));

    [Fact]
    public void LocalPath_StaysInsideTheClone()
    {
        var root = Path.Combine(Path.GetTempPath(), "clone");
        Assert.Equal(Path.Combine(root, "src", "a.cs"), AzureDevOpsWebUrl.LocalPath(root, "/src/a.cs"));
        Assert.Null(AzureDevOpsWebUrl.LocalPath(root, "/../outside.txt"));
        Assert.Null(AzureDevOpsWebUrl.LocalPath(root, "/"));
    }

    [Fact]
    public void Catalog_PullRequestPage_OffersDiffAndDocument()
    {
        var page = BrowserSupportCatalog.Describe("https://dev.azure.com/org/Proj/_git/Repo/pullrequest/12");
        Assert.Equal(BrowserSupportCatalog.AzureDevOpsSite, page!.Site);
        Assert.Equal("PR 12", page.Heading);
        Assert.Equal(
            [BrowserSupportActionKind.PullRequestDiff, BrowserSupportActionKind.PullRequestDocument, BrowserSupportActionKind.SelectRepository],
            page.Actions.Select(a => a.Kind));
    }

    [Fact]
    public void Catalog_FilePage_OpensTheLine()
    {
        var page = BrowserSupportCatalog.Describe("https://dev.azure.com/org/Proj/_git/Repo?path=/src/Main.cs&version=GBmain&line=9");
        Assert.Equal("Main.cs:9", page!.Heading);
        Assert.Contains("ブランチ main", page.Detail);
        Assert.Equal(BrowserSupportActionKind.OpenLocalFile, page.Actions[0].Kind);
        Assert.Contains("9 行目", page.Actions[0].Label);
    }

    [Fact]
    public void Catalog_UnknownPage_IsNull() => Assert.Null(BrowserSupportCatalog.Describe("https://learn.microsoft.com/"));

    [Fact]
    public void ViewModel_HidesActionsThatNeedAClone_UntilOneIsFound()
    {
        var vm = new BrowserSupportViewModel();
        const string url = "https://dev.azure.com/org/Proj/_git/Repo/pullrequest/12";
        var page = BrowserSupportCatalog.Describe(url);
        Assert.True(vm.Show(page, url));
        // クローンが分かる前は、要らない道具（会話を読む）だけ。
        Assert.Equal([BrowserSupportActionKind.PullRequestDocument], vm.Actions.Select(a => a.Action.Kind));

        vm.SetRepository(page!, @"C:\work\repo", "手元のクローン");
        Assert.Equal(3, vm.Actions.Count);

        vm.SetRepository(page!, null, "ありません");
        Assert.Single(vm.Actions);
    }

    [Fact]
    public void ViewModel_SamePage_IsNotRebuilt()
    {
        var vm = new BrowserSupportViewModel();
        const string url = "https://dev.azure.com/org/Proj/_git/Repo/pullrequest/12";
        Assert.True(vm.Show(BrowserSupportCatalog.Describe(url), url));
        var shown = vm.Page!;
        vm.SetRepository(shown, @"C:\work\repo", "");
        // 同じ PR の別タブ（?_a=files）や題の確定では作り直さない（見つけたクローンを捨てない）。
        Assert.False(vm.Show(BrowserSupportCatalog.Describe(url + "?_a=files"), url + "?_a=files"));
        Assert.Equal(@"C:\work\repo", vm.RepositoryRoot);

        vm.Forget();
        Assert.True(vm.Show(BrowserSupportCatalog.Describe(url), url));
        Assert.Null(vm.RepositoryRoot);
    }

    [Fact]
    public void ViewModel_ResultForAnOldPage_IsIgnored()
    {
        var vm = new BrowserSupportViewModel();
        var first = BrowserSupportCatalog.Describe("https://dev.azure.com/org/Proj/_git/Repo/pullrequest/1")!;
        vm.Show(first, "a");
        vm.Show(BrowserSupportCatalog.Describe("https://dev.azure.com/org/Proj/_git/Repo/pullrequest/2"), "b");
        vm.SetRepository(first, @"C:\work\repo", "古い答え");
        Assert.Null(vm.RepositoryRoot);
    }
}
