using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>「問題」一覧から抜粋タブ（§35.3）へ流す行の選び方のテスト。</summary>
public class ProblemExcerptsTests
{
    private static string Build(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void Displayed_follows_tree_order_and_converts_to_zero_based_lines()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(Build(
            @"C:\src\B.cs(7,1): warning CS0219: 未使用 [C:\src\P.csproj]",
            @"C:\src\A.cs(3,5): error CS8618: null 非許容 [C:\src\P.csproj]",
            @"C:\src\A.cs(1,1): warning CS0219: 未使用 [C:\src\P.csproj]"));

        var plan = vm.PlanDisplayedExcerpts();

        // エラーを含む A.cs が先、ファイル内は行順。行は 0 始まりへ。
        Assert.Equal(
            [(@"C:\src\A.cs", 0), (@"C:\src\A.cs", 2), (@"C:\src\B.cs", 6)],
            plan.Requests.Select(r => (r.Path, r.Line)).ToArray());
        Assert.Equal("問題: 表示中 (3)", plan.Title);
        Assert.Equal(3, plan.ProblemCount);
        Assert.Equal(0, plan.OutsideCount);
    }

    [Fact]
    public void Displayed_respects_severity_filter()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(Build(
            @"C:\src\A.cs(3,5): error CS8618: null 非許容 [C:\src\P.csproj]",
            @"C:\src\B.cs(1,1): warning CS0219: 未使用 [C:\src\P.csproj]"));
        vm.ShowWarnings = false;

        var plan = vm.PlanDisplayedExcerpts();

        Assert.Equal([@"C:\src\A.cs"], plan.Requests.Select(r => r.Path).ToArray());
        Assert.Equal("問題: 表示中 (1)", plan.Title);
    }

    [Fact]
    public void SameCode_collects_every_file_even_when_scope_is_current_file()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(Build(
            @"C:\src\A.cs(3,5): error CS8618: null 非許容 'X' [C:\src\P.csproj]",
            @"C:\src\A.cs(9,5): error CS1002: ; が必要です [C:\src\P.csproj]",
            @"C:\src\B.cs(4,1): error cs8618: null 非許容 'Y' [C:\src\P.csproj]",
            @"C:\src\C.cs(2,1): error CS8618: null 非許容 'Z' [C:\src\P.csproj]"));
        vm.CurrentFilePath = @"C:\src\A.cs";
        vm.Scope = ProblemScope.CurrentFile;
        var target = vm.Groups.Single().Items.First(i => i.Code == "CS8618");

        var plan = vm.PlanSameCodeExcerpts(target);

        // 範囲（現在のファイル）は外し、コードは大文字小文字を問わず同一視する。CS1002 は入らない。
        Assert.Equal(
            [(@"C:\src\A.cs", 2), (@"C:\src\B.cs", 3), (@"C:\src\C.cs", 1)],
            plan.Requests.Select(r => (r.Path, r.Line)).ToArray());
        Assert.Equal("問題: CS8618 (3)", plan.Title);
    }

    [Fact]
    public void SameCode_still_respects_severity_and_source_filters()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(Build(
            @"C:\src\A.cs(3,5): warning CS8618: null 非許容 [C:\src\P.csproj]",
            @"C:\src\B.cs(4,1): warning CS8618: null 非許容 [C:\src\P.csproj]"));
        var target = vm.Groups.First().Items[0];
        vm.ShowBuild = false;

        Assert.Empty(vm.PlanSameCodeExcerpts(target).Requests);
    }

    [Fact]
    public void Codeless_diagnostics_match_by_message_not_by_source_name()
    {
        var a1 = new ProblemItemViewModel(@"C:\src\a.md", 1, 1, ProblemSeverity.Warning, "markdownlint", "見出しが重複",
            ProblemSource.Lsp, hasCode: false);
        var a2 = new ProblemItemViewModel(@"C:\src\b.md", 5, 1, ProblemSeverity.Warning, "markdownlint", "見出しが重複",
            ProblemSource.Lsp, hasCode: false);
        var other = new ProblemItemViewModel(@"C:\src\c.md", 2, 1, ProblemSeverity.Warning, "markdownlint", "行が長い",
            ProblemSource.Lsp, hasCode: false);
        var coded = new ProblemItemViewModel(@"C:\src\d.md", 2, 1, ProblemSeverity.Warning, "markdownlint", "見出しが重複",
            ProblemSource.Lsp);

        var plan = ProblemExcerpts.SameCode(a1, [a1, a2, other, coded], _ => true);

        Assert.Equal([@"C:\src\a.md", @"C:\src\b.md"], plan.Requests.Select(r => r.Path).ToArray());
        Assert.Equal("問題: 見出しが重複 (2)", plan.Title);
        Assert.Equal("同じメッセージの問題をすべて抜粋タブで開く", a1.SameCodeExcerptHeader);
        Assert.Equal("markdownlint をすべて抜粋タブで開く", coded.SameCodeExcerptHeader);
    }

    [Fact]
    public void File_plan_accepts_group_or_row_and_titles_with_file_name()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(Build(
            @"C:\src\A.cs(3,5): error CS8618: null 非許容 [C:\src\P.csproj]",
            @"C:\src\A.cs(8,5): warning CS0219: 未使用 [C:\src\P.csproj]",
            @"C:\src\B.cs(4,1): error CS1002: ; が必要です [C:\src\P.csproj]"));
        var groupA = vm.Groups.First(g => g.FileName == "A.cs");

        var fromGroup = vm.PlanFileExcerpts(groupA)!;
        var fromRow = vm.PlanFileExcerpts(groupA.Items[1])!;

        Assert.Equal("問題: A.cs (2)", fromGroup.Title);
        Assert.Equal([2, 7], fromGroup.Requests.Select(r => r.Line).ToArray());
        Assert.Equal(fromGroup.Requests, fromRow.Requests);
        Assert.Null(vm.PlanFileExcerpts(null));
    }

    [Fact]
    public void Files_outside_every_workspace_folder_are_left_out_and_counted()
    {
        var ws = new FakeWorkspaceService(@"C:\work\app");
        ws.AddFolder(@"C:\work\lib");
        var vm = new ProblemsViewModel(ws);
        vm.SetFromBuildOutput(Build(
            @"C:\work\app\A.cs(3,5): warning MSB3026: コピー失敗 [C:\work\app\P.csproj]",
            // 追加フォルダーの中は入る（マルチルート）。
            @"C:\work\lib\L.cs(2,1): warning MSB3026: コピー失敗 [C:\work\lib\L.csproj]",
            // 区切り無しの前方一致で app 配下と誤認しない。
            @"C:\work\app2\X.cs(1,1): warning MSB3026: コピー失敗 [C:\work\app2\X.csproj]",
            @"C:\Program Files\dotnet\sdk\Microsoft.Common.CurrentVersion.targets(5094,5): warning MSB3026: コピー失敗 [C:\work\app\P.csproj]"));

        var plan = vm.PlanDisplayedExcerpts();

        Assert.Equal(
            [@"C:\work\app\A.cs", @"C:\work\lib\L.cs"],
            plan.Requests.Select(r => r.Path).Order().ToArray());
        Assert.Equal(2, plan.OutsideCount);
        Assert.Equal("問題: 表示中 (2)", plan.Title);
    }

    [Fact]
    public void Commands_raise_excerpt_requested()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\src\A.cs(3,5): error CS8618: null 非許容 [C:\src\P.csproj]");
        var plans = new List<ProblemExcerptPlan>();
        vm.ExcerptRequested += plans.Add;
        var item = vm.Groups[0].Items[0];

        vm.OpenDisplayedAsExcerptsCommand.Execute(null);
        vm.OpenSameCodeAsExcerptsCommand.Execute(item);
        vm.OpenFileAsExcerptsCommand.Execute(vm.Groups[0]);

        Assert.Equal(["問題: 表示中 (1)", "問題: CS8618 (1)", "問題: A.cs (1)"], plans.Select(p => p.Title).ToArray());
    }
}
