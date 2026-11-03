using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.GitHub;

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
    public void Catalog_OtherPages()
    {
        var generic = BrowserSupportCatalog.Describe("https://learn.microsoft.com/ja-jp/dotnet/")!;
        Assert.True(generic.IsGeneric);
        Assert.True(generic.ShowsCode);
        Assert.Empty(generic.Actions);
        Assert.Null(BrowserSupportCatalog.Describe("about:blank"));
        Assert.Null(BrowserSupportCatalog.Describe(""));
    }

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

    // ── Azure Pipelines ──────────────────────────────────────────

    [Fact]
    public void AzureBuildPage()
    {
        var page = AzureDevOpsWebUrl.TryParse("https://dev.azure.com/org/Proj/_build/results?buildId=4567&view=logs");
        Assert.Equal(AzureDevOpsWebPageKind.Build, page!.Kind);
        Assert.Equal(("https://dev.azure.com/org", "Proj", 4567), (page.OrganizationUrl, page.Project, page.BuildId));
        var described = BrowserSupportCatalog.Describe("https://dev.azure.com/org/Proj/_build/results?buildId=4567")!;
        Assert.False(described.HasRepository);
        Assert.Equal(BrowserSupportActionKind.BuildLog, Assert.Single(described.Actions).Kind);
    }

    [Fact]
    public void AzureTimeline_KeepsFailedStepsAndIssues()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"records":[
              {"name":"Build","type":"Task","result":"failed","order":3,"log":{"id":12},
               "issues":[{"type":"error","message":"D:\\a\\1\\s\\src\\A.cs(10,5): error CS0103: x","data":{"sourcepath":"D:\\a\\1\\s\\src\\A.cs","linenumber":"10","columnnumber":"5"}}]},
              {"name":"Checkout","type":"Task","result":"succeeded","log":{"id":3}},
              {"name":"Lint","type":"Task","result":"succeededWithIssues","issues":[{"type":"warning","message":"w"}]}
            ]}
            """);
        var steps = AzureDevOpsBuildJson.ParseTimeline(doc.RootElement);
        Assert.Equal(["Build", "Lint"], steps.Select(s => s.Name));
        Assert.True(steps[0].Failed);
        Assert.Equal(12, steps[0].LogId);
        var issue = Assert.Single(steps[0].Issues);
        Assert.Equal((@"D:\a\1\s\src\A.cs", 10, 5), (issue.SourcePath, issue.Line, issue.Column));
        Assert.False(steps[1].Failed);
    }

    // ── CI のログ ───────────────────────────────────────────────

    [Fact]
    public void CiLog_MsBuildAndGccForms()
    {
        var log = string.Join("\n",
            @"2026-11-03T01:02:03.4567890Z ##[error]D:\a\1\s\src\App\Main.cs(12,5): error CS0103: 名前 'x' は存在しません [D:\a\1\s\src\App\App.csproj]",
            "build\tRun tests\t2026-11-03T01:02:03.0000000Z src/lib/a.ts(3,1): error TS2304: Cannot find name 'foo'.",
            "src/main.c:7:9: warning: unused variable",
            "Connecting to example.com:443 ... ok",
            "error: something without a place");
        var problems = CiLogProblems.Extract(log);
        Assert.Equal(3, problems.Count);
        Assert.Equal((@"D:\a\1\s\src\App\Main.cs", 12, 5, "error"), (problems[0].Path, problems[0].Line, problems[0].Column, problems[0].Severity));
        Assert.StartsWith("CS0103", problems[0].Message);
        Assert.Equal(("src/lib/a.ts", 3), (problems[1].Path, problems[1].Line));
        Assert.Equal(("src/main.c", 7, "warning"), (problems[2].Path, problems[2].Line, problems[2].Severity));
    }

    [Fact]
    public void CiLog_MapsAgentPathToTheLongestSuffixInTheClone()
    {
        var root = Path.Combine(Path.GetTempPath(), "clone");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(root, "src", "App", "Main.cs"),
            Path.Combine(root, "Main.cs"),
        };
        Assert.Equal(Path.Combine(root, "src", "App", "Main.cs"),
            CiLogProblems.MapToClone(root, @"D:\a\1\s\src\App\Main.cs", present.Contains));
        Assert.Equal(Path.Combine(root, "src", "App", "Main.cs"),
            CiLogProblems.MapToClone(root, "/home/runner/work/repo/repo/src/App/Main.cs", present.Contains));
        Assert.Null(CiLogProblems.MapToClone(root, "/x/Other.cs", present.Contains));
        Assert.Null(CiLogProblems.MapToClone(root, "../../etc/passwd", _ => true));
    }

    // ── GitHub ─────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/o/r/pull/12/files", GitHubWebPageKind.PullRequest, 12)]
    [InlineData("https://github.com/o/r/issues/34", GitHubWebPageKind.Issue, 34)]
    [InlineData("https://github.com/o/r", GitHubWebPageKind.Repository, 0)]
    public void GitHub_Pages(string url, GitHubWebPageKind kind, int number)
    {
        var page = GitHubWebUrl.TryParse(url)!;
        Assert.Equal((kind, number, "o/r", "https://github.com/o/r"), (page.Kind, page.Number, page.Slug, page.RepositoryUrl));
    }

    [Fact]
    public void GitHub_CommitsFilesAndRuns()
    {
        Assert.Equal("abcdef1234", GitHubWebUrl.TryParse("https://github.com/o/r/commit/abcdef1234")!.CommitSha);
        var inPr = GitHubWebUrl.TryParse("https://github.com/o/r/pull/5/commits/abcdef1234")!;
        Assert.Equal((GitHubWebPageKind.Commit, 5), (inPr.Kind, inPr.Number));
        var file = GitHubWebUrl.TryParse("https://github.com/o/r/blob/feature/x/src/a.cs#L42-L50")!;
        Assert.Equal((GitHubWebPageKind.File, "feature/x/src/a.cs", 42, "a.cs"), (file.Kind, file.BlobPath, file.Line, file.FileName));
        var run = GitHubWebUrl.TryParse("https://github.com/o/r/actions/runs/987654321/job/1")!;
        Assert.Equal((GitHubWebPageKind.ActionsRun, 987654321L), (run.Kind, run.RunId));
        Assert.Null(GitHubWebUrl.TryParse("https://github.com/settings/profile"));
        Assert.Null(GitHubWebUrl.TryParse("https://gitlab.com/o/r"));
    }

    [Fact]
    public void GitHub_BlobPath_FindsTheRefBoundaryByTheFileInTheClone()
    {
        var root = Path.Combine(Path.GetTempPath(), "clone");
        var file = Path.Combine(root, "src", "a.cs");
        Assert.Equal(file, GitHubWebUrl.LocalPath(root, "feature/x/src/a.cs", p => p == file));
        Assert.Equal(file, GitHubWebUrl.LocalPath(root, "main/src/a.cs", p => p == file));
        Assert.Null(GitHubWebUrl.LocalPath(root, "main/../x", _ => true));
    }

    // ── パッケージ ─────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.npmjs.com/package/react", PackageEcosystem.Npm, "react", null)]
    [InlineData("https://www.npmjs.com/package/@types/node/v/22.1.0", PackageEcosystem.Npm, "@types/node", "22.1.0")]
    [InlineData("https://www.nuget.org/packages/Newtonsoft.Json/13.0.3", PackageEcosystem.NuGet, "Newtonsoft.Json", "13.0.3")]
    [InlineData("https://www.nuget.org/packages/Serilog", PackageEcosystem.NuGet, "Serilog", null)]
    public void PackagePages(string url, PackageEcosystem ecosystem, string name, string? version)
        => Assert.Equal(new PackagePage(ecosystem, name, version), BrowserPackagePages.TryParse(url));

    [Fact]
    public void Package_ReadsNpmSections()
    {
        const string json = "{\n  \"name\": \"web\",\n  \"dependencies\": { \"react\": \"^18.2.0\" },\n  \"devDependencies\": { \"vite\": \"5.0.0\" }\n}";
        var usage = Assert.Single(BrowserPackagePages.ReadNpm("package.json", json, "vite"));
        Assert.Equal(("5.0.0", "devDependencies", 4), (usage.Version, usage.Section, usage.Line));
        Assert.Empty(BrowserPackagePages.ReadNpm("package.json", json, "lodash"));
    }

    [Fact]
    public void Package_ReadsNuGetReferencesAndCentralVersions()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="serilog" Version="3.1.1" />
                <PackageReference Include="Other"><Version>1.0.0</Version></PackageReference>
              </ItemGroup>
            </Project>
            """;
        var usage = Assert.Single(BrowserPackagePages.ReadNuGet("a.csproj", csproj, "Serilog"));
        Assert.Equal(("3.1.1", "PackageReference", 3, true), (usage.Version, usage.Section, usage.Line, usage.CanSetVersion));
        Assert.Equal("1.0.0", Assert.Single(BrowserPackagePages.ReadNuGet("a.csproj", csproj, "Other")).Version);
        const string props = """<Project><ItemGroup><PackageVersion Include="Serilog" Version="3.1.1" /></ItemGroup></Project>""";
        Assert.False(Assert.Single(BrowserPackagePages.ReadNuGet("Directory.Packages.props", props, "Serilog")).CanSetVersion);
    }

    [Fact]
    public void Package_InstallCommands()
    {
        var npm = new PackagePage(PackageEcosystem.Npm, "@types/node", "22.1.0");
        Assert.Equal(@"Push-Location -LiteralPath 'C:\w\web'; npm install '@types/node@22.1.0' --save-dev; Pop-Location",
            BrowserPackagePages.InstallCommand(npm, @"C:\w\web\package.json", "22.1.0", "devDependencies"));
        var nuget = new PackagePage(PackageEcosystem.NuGet, "Serilog", null);
        Assert.Equal(@"dotnet add 'C:\w\it''s\a.csproj' package Serilog", BrowserPackagePages.InstallCommand(nuget, @"C:\w\it's\a.csproj", null));
        Assert.False(BrowserPackagePages.IsSafeVersion(">=1.0; rm"));
        Assert.False(BrowserPackagePages.IsSafeName("x;calc"));
    }

    [Fact]
    public void Package_ScanFindsUsagesAndOtherProjects()
    {
        var root = Directory.CreateTempSubdirectory("loomo-pkg").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "bin"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.WriteAllText(Path.Combine(root, "a", "a.csproj"), """<Project><ItemGroup><PackageReference Include="Serilog" Version="3.0.0" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(root, "b", "b.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "a", "bin", "copy.csproj"), """<Project><ItemGroup><PackageReference Include="Serilog" Version="1" /></ItemGroup></Project>""");
            var scan = BrowserPackagePages.Scan([root], new PackagePage(PackageEcosystem.NuGet, "Serilog", "4.0.0"));
            Assert.Equal(Path.Combine(root, "a", "a.csproj"), Assert.Single(scan.Usages).FilePath);
            Assert.Equal(Path.Combine(root, "b", "b.csproj"), Assert.Single(scan.OtherProjects));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── ページのコード・localhost ───────────────────────────────

    [Fact]
    public void PageCode_ParsesScriptResultAndCommands()
    {
        var inner = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { lang = "bash", text = "$ npm install vite" },
            new { lang = "csharp", text = "var x = 1;\nConsole.WriteLine(x);" },
            new { lang = "", text = "   " },
        });
        var blocks = BrowserPageCode.Parse(System.Text.Json.JsonSerializer.Serialize(inner));
        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[0].LooksLikeCommand);
        Assert.False(blocks[1].LooksLikeCommand);
        Assert.Equal("npm install vite", BrowserPageCode.AsCommand(blocks[0].Text));
        Assert.Equal("dotnet run", BrowserPageCode.AsCommand(@"PS C:\src> dotnet run"));
        Assert.Equal(".cs", BrowserPageCode.ExtensionFor("csharp"));
        Assert.Equal(".txt", BrowserPageCode.ExtensionFor("unknown"));
        Assert.Empty(BrowserPageCode.Parse("null"));
        Assert.Empty(BrowserPageCode.Parse("not json"));
    }

    [Fact]
    public void Catalog_LocalhostPackageAndGitHubPages()
    {
        var local = BrowserSupportCatalog.Describe("http://localhost:5173/app")!;
        Assert.Equal(5173, local.LocalPort);
        Assert.False(local.ShowsCode);
        Assert.Equal(5173, BrowserSupportCatalog.Describe("http://127.0.0.1:5173/")!.LocalPort);
        var package = BrowserSupportCatalog.Describe("https://www.nuget.org/packages/Serilog/4.0.0")!;
        Assert.NotNull(package.Package);
        Assert.True(package.ShowsCode);   // README の例
        var gh = BrowserSupportCatalog.Describe("https://github.com/o/r/pull/3")!;
        Assert.True(gh.HasRepository);
        Assert.False(gh.ShowsCode);
        Assert.Equal([BrowserSupportActionKind.PullRequestDiff, BrowserSupportActionKind.PullRequestDocument, BrowserSupportActionKind.SelectRepository],
            gh.Actions.Select(a => a.Kind));
    }

    [Fact]
    public void ListeningPorts_NetworkByteOrder() => Assert.Equal(5173, ListeningPorts.NetworkPort(((5173 & 0xFF) << 8) | (5173 >> 8)));

    [Fact]
    public void ViewModel_SectionsAreOrderedAndDroppedWithThePage()
    {
        var vm = new BrowserSupportViewModel();
        var page = BrowserSupportCatalog.Describe("https://example.com/a")!;
        vm.Show(page, "https://example.com/a");
        vm.SetSection(page, "clips", "抜き書き", 70, [new BrowserSupportRow("x", "", [])]);
        vm.SetSection(page, "code", "コード", 60, [new BrowserSupportRow("y", "", [])]);
        Assert.Equal(["code", "clips"], vm.Sections.Select(s => s.Key));
        vm.SetSection(page, "code", "コード", 60, [], "なし");
        Assert.Empty(vm.Sections[0].Rows);
        Assert.True(vm.Sections[0].HasNote);
        vm.RemoveSection(page, "clips");
        Assert.Single(vm.Sections);

        var next = BrowserSupportCatalog.Describe("https://example.com/b")!;
        vm.Show(next, "https://example.com/b");
        Assert.Empty(vm.Sections);
        Assert.Null(vm.SetSection(page, "code", "コード", 60, []));   // 前のページの答えは捨てる
        Assert.Empty(vm.Sections);
    }

    // ── 調べ始めた場所へ貼る（§41.12） ──

    [Fact]
    public void PasteBelow_IndentsToAnchorLine_AndStripsCommonIndent()
    {
        var doc = "class A {\n    void M() {\n        Foo();\n    }\n}";
        var paste = BrowserSearchOrigin.PasteBelow(doc, 2, "    var x = 1;\n    if (x > 0)\n        Bar();\n");
        Assert.Equal(2, paste.Line);
        Assert.Equal("        Foo();".Length, paste.Column);   // 行末に差し込む
        Assert.Equal("\n        var x = 1;\n        if (x > 0)\n            Bar();", paste.Text);
        Assert.Equal(3, paste.FirstInsertedLine);
        Assert.Equal(5, paste.LastInsertedLine);
    }

    [Fact]
    public void PasteBelow_UsesDocumentNewline_AndKeepsBlankLinesEmpty()
    {
        var paste = BrowserSearchOrigin.PasteBelow("a\r\n\tb\r\nc", 1, "x\n\ny");
        Assert.Equal(2, paste.Column);   // 行末の CR は数えない
        Assert.Equal("\r\n\tx\r\n\r\n\ty", paste.Text);
    }

    [Fact]
    public void PasteBelow_ClampsLineOutsideDocument()
    {
        var paste = BrowserSearchOrigin.PasteBelow("one\ntwo", 10, "z");
        Assert.Equal(1, paste.Line);
        Assert.Equal("\nz", paste.Text);
    }

    [Fact]
    public void SearchOrigin_QueryPreview_IsOneLineAndShort()
    {
        var origin = new BrowserSearchOrigin.Terminal("error CS0103:\n" + new string('x', 100), Guid.NewGuid());
        Assert.DoesNotContain("\n", origin.QueryPreview);
        Assert.Equal(60, origin.QueryPreview.Length);
        Assert.EndsWith("…", origin.QueryPreview);
    }

}

