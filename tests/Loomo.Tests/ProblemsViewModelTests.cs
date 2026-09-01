using sk0ya.Loomo.App.ViewModels;
using Editor.Core.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>「問題」タブ（IDE ペイン）のビルド出力パースのテスト。</summary>
public class ProblemsViewModelTests
{
    [Fact]
    public void ParseBuildOutput_extracts_error_and_warning_lines()
    {
        var output = string.Join("\r\n",
            @"  復元対象のプロジェクトを決定しています...",
            @"C:\Projects\Loomo\src\Loomo.Core\Agent\AgentOrchestrator.cs(12,34): error CS1002: ; が必要です [C:\Projects\Loomo\src\Loomo.Core\Loomo.Core.csproj]",
            @"C:\Projects\Loomo\src\Loomo.App\Views\ShellWindow.xaml.cs(5,1): warning CS0219: 変数 'x' は割り当てられていますが、値は使用されていません [C:\Projects\Loomo\src\Loomo.App\Loomo.App.csproj]",
            @"ビルドに失敗しました。");

        var items = ProblemsViewModel.ParseBuildOutput(output);

        Assert.Equal(2, items.Count);
        Assert.Equal(ProblemSeverity.Error, items[0].Severity);
        Assert.Equal("CS1002", items[0].Code);
        Assert.Equal(@"C:\Projects\Loomo\src\Loomo.Core\Agent\AgentOrchestrator.cs", items[0].FilePath);
        Assert.Equal(12, items[0].Line1);
        Assert.Equal(34, items[0].Column1);
        Assert.Equal("; が必要です", items[0].Message);   // 末尾の [proj] は落ちる
        Assert.Equal(ProblemSeverity.Warning, items[1].Severity);
        Assert.Equal("CS0219", items[1].Code);
    }

    [Fact]
    public void ParseBuildOutput_dedupes_summary_repeats()
    {
        // MSBuild はエラーを本文とサマリ節（インデント付き）で再掲する。
        var output = string.Join("\n",
            @"C:\src\A.cs(1,2): error CS0246: 型が見つかりません [C:\src\P.csproj]",
            @"    C:\src\A.cs(1,2): error CS0246: 型が見つかりません [C:\src\P.csproj]");

        var items = ProblemsViewModel.ParseBuildOutput(output);

        Assert.Single(items);
    }

    [Fact]
    public void ParseBuildOutput_matches_msbuild_and_xaml_codes()
    {
        var output = string.Join("\n",
            @"C:\Program Files\dotnet\sdk\9.0.300\Microsoft.Common.CurrentVersion.targets(5094,5): warning MSB3026: コピーできませんでした。 [C:\src\P.csproj]",
            @"C:\src\Views\MainWindow.xaml(10,5): error MC3000: XML が無効です。 [C:\src\P.csproj]");

        var items = ProblemsViewModel.ParseBuildOutput(output);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Code == "MSB3026" && i.Severity == ProblemSeverity.Warning);
        Assert.Contains(items, i => i.Code == "MC3000" && i.Severity == ProblemSeverity.Error);
    }

    [Fact]
    public void ParseBuildOutput_ignores_non_diagnostic_lines()
    {
        var output = string.Join("\n",
            "MSBuild のバージョン 17.0",
            "    0 個の警告",
            "    0 エラー",
            "経過時間 00:00:03.00",
            @"  Loomo.Core -> C:\Projects\Loomo\src\Loomo.Core\bin\Debug\net9.0\sk0ya.Loomo.Core.dll");

        Assert.Empty(ProblemsViewModel.ParseBuildOutput(output));
    }

    [Fact]
    public void SetFromBuildOutput_groups_by_file_errors_first_and_counts()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(string.Join("\n",
            @"C:\src\B.cs(3,1): warning CS0219: 未使用 [C:\src\P.csproj]",
            @"C:\src\A.cs(9,1): error CS1002: ; が必要です [C:\src\P.csproj]",
            @"C:\src\A.cs(2,1): warning CS0168: 未使用 [C:\src\P.csproj]"));

        Assert.True(vm.HasItems);
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(2, vm.WarningCount);
        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("A.cs", vm.Groups[0].FileName);            // エラーを含むファイルが先
        Assert.Equal(2, vm.Groups[0].Items.Count);
        Assert.Equal(2, vm.Groups[0].Items[0].Line1);           // 配下は行順
        Assert.True(vm.Groups[0].HasErrors);
        Assert.False(vm.Groups[1].HasErrors);

        // きれいなビルド出力で空に戻る。
        vm.SetFromBuildOutput("    0 個の警告\n    0 エラー");
        Assert.False(vm.HasItems);
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public void SetFromBuildOutput_preserves_expansion_state_by_path()
    {
        var vm = new ProblemsViewModel();
        var output = @"C:\src\A.cs(9,1): error CS1002: ; が必要です [C:\src\P.csproj]";
        vm.SetFromBuildOutput(output);
        Assert.True(vm.Groups[0].IsExpanded);                   // 既定は展開

        vm.Groups[0].IsExpanded = false;
        vm.SetFromBuildOutput(output);                          // 再ビルド相当
        Assert.False(vm.Groups[0].IsExpanded);                  // 畳んだ状態を引き継ぐ
    }

    [Fact]
    public void ParseBuildOutput_matches_tsc_diagnostics_and_absolutizes_with_baseDir()
    {
        // tsc --pretty false は cwd 相対パスで path(line,col): error TSxxxx: message を出す。
        var output = string.Join("\n",
            "src/index.ts(7,5): error TS2322: Type 'string' is not assignable to type 'number'.",
            "src/util.ts(3,10): warning TS6133: 'x' is declared but its value is never read.",
            "Found 1 error.");

        var items = ProblemsViewModel.ParseBuildOutput(output, baseDir: @"C:\work\app");

        Assert.Equal(2, items.Count);
        Assert.Equal(ProblemSeverity.Error, items[0].Severity);
        Assert.Equal("TS2322", items[0].Code);
        Assert.Equal(@"C:\work\app\src\index.ts", items[0].FilePath);   // baseDir 基準で絶対化
        Assert.Equal(7, items[0].Line1);
        Assert.Equal(5, items[0].Column1);
        Assert.Equal("TS6133", items[1].Code);
    }

    [Fact]
    public void ParseBuildOutput_baseDir_keeps_already_rooted_paths()
    {
        var items = ProblemsViewModel.ParseBuildOutput(
            @"C:\abs\a.ts(1,1): error TS1005: ';' expected.", baseDir: @"C:\work\app");

        Assert.Equal(@"C:\abs\a.ts", Assert.Single(items).FilePath);
    }

    [Fact]
    public void Group_relative_dir_uses_workspace_root()
    {
        var ws = new FakeWorkspaceService();
        ws.OpenFolder(@"C:\src");
        var vm = new ProblemsViewModel(ws);
        vm.SetFromBuildOutput(string.Join("\n",
            @"C:\src\Sub\Deep\A.cs(1,1): error CS1002: ; が必要です [C:\src\P.csproj]",
            @"C:\src\B.cs(1,1): error CS1002: ; が必要です [C:\src\P.csproj]"));

        Assert.Equal("Sub/Deep", vm.Groups.First(g => g.FileName == "A.cs").RelativeDir);
        Assert.Equal("", vm.Groups.First(g => g.FileName == "B.cs").RelativeDir);   // ルート直下は空
    }

    [Fact]
    public void Lsp_diagnostics_are_merged_cleared_and_identify_their_source()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\src\A.cs(2,3): warning CS0168: unused [C:\src\P.csproj]");
        var uri = new Uri(@"C:\src\B.cs").AbsoluteUri;

        vm.SetLspDiagnostics(uri,
        [
            new(new(new(4, 6), new(4, 8)), "型が見つかりません", DiagnosticSeverity.Error, "csharp"),
            new(new(new(0, 0), new(0, 1)), "ヒント", DiagnosticSeverity.Hint, "csharp"),
        ]);

        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);
        Assert.Equal(0, vm.InformationCount);
        Assert.Equal(1, vm.HintCount);
        var lsp = vm.Groups.Single(g => g.FileName == "B.cs").Items
            .Single(item => item.Severity == ProblemSeverity.Error);
        Assert.Equal(5, lsp.Line1);
        Assert.Equal(7, lsp.Column1);
        Assert.Equal(ProblemSource.Lsp, lsp.Source);
        Assert.Equal("LSP", lsp.SourceLabel);

        vm.SetLspDiagnostics(uri, []);
        Assert.DoesNotContain(vm.Groups, g => g.FileName == "B.cs");
        Assert.Single(vm.Groups);
    }

    [Fact]
    public void Compiler_fallback_diagnostics_have_their_own_source_filter()
    {
        var vm = new ProblemsViewModel();
        vm.SetCompilerDiagnostics(@"C:\src\A.cs", [
            new(new(new(1, 2), new(1, 3)), "; が必要です", DiagnosticSeverity.Error,
                "Compiler", "CS1002")]);

        var item = Assert.Single(vm.Groups.SelectMany(group => group.Items));
        Assert.Equal(ProblemSource.Compiler, item.Source);
        Assert.Equal("Compiler", item.SourceLabel);

        vm.ShowCompiler = false;
        Assert.Empty(vm.Groups);
        vm.ShowCompiler = true;
        Assert.Single(vm.Groups.SelectMany(group => group.Items));

        vm.ClearAllCompilerDiagnostics();
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public void Lsp_copy_wins_over_compiler_fallback_for_the_same_diagnostic()
    {
        var vm = new ProblemsViewModel();
        const string path = @"C:\src\A.cs";
        vm.SetCompilerDiagnostics(path, [
            new(new(new(0, 0), new(0, 1)), "同じ診断", DiagnosticSeverity.Error,
                "Compiler", "CS1002")]);
        vm.SetLspDiagnostics(new Uri(path).AbsoluteUri, [
            new(new(new(0, 0), new(0, 1)), "同じ診断", DiagnosticSeverity.Error,
                "csharp", "CS1002")]);

        var item = Assert.Single(vm.Groups.SelectMany(group => group.Items));
        Assert.Equal(ProblemSource.Lsp, item.Source);
    }

    [Fact]
    public void Lsp_diagnostics_land_on_the_right_file_when_the_server_encodes_the_drive_colon()
    {
        // tsserver 系の "file:///c%3A/src/App.ts"。Uri.LocalPath 直読みだと "/c:/src/App.ts" →
        // GetFullPath で "C:\c:\src\App.ts" となり、問題パネルの行から飛べなくなる。
        var vm = new ProblemsViewModel();

        vm.SetLspDiagnostics("file:///c%3A/src/App.ts",
        [
            new(new(new(2, 0), new(2, 4)), "型が合いません", DiagnosticSeverity.Error, "ts"),
        ]);

        var group = Assert.Single(vm.Groups);
        Assert.Equal("App.ts", group.FileName);
        Assert.Equal(@"c:\src\App.ts", Assert.Single(group.Items).FilePath, ignoreCase: true);
    }

    [Fact]
    public void Filters_apply_severity_source_and_current_file_without_losing_backing_items()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(string.Join("\n",
            @"C:\src\A.cs(1,1): error CS1001: build error [C:\src\P.csproj]",
            @"C:\src\B.cs(2,1): warning CS1002: build warning [C:\src\P.csproj]"));
        vm.SetLspDiagnostics(new Uri(@"C:\src\A.cs").AbsoluteUri,
        [
            new(new(new(2, 0), new(2, 1)), "lsp warning", DiagnosticSeverity.Warning, "csharp"),
        ]);

        vm.ShowBuild = false;
        Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSource.Lsp, vm.Groups[0].Items[0].Source);

        vm.ShowBuild = true;
        vm.ShowWarnings = false;
        Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSeverity.Error, vm.Groups[0].Items[0].Severity);

        vm.ShowWarnings = true;
        vm.CurrentFilePath = @"C:\src\B.cs";
        vm.Scope = ProblemScope.CurrentFile;
        Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal("B.cs", vm.Groups[0].FileName);

        vm.Scope = ProblemScope.Workspace;
        Assert.Equal(3, vm.Groups.SelectMany(g => g.Items).Count());
    }

    [Fact]
    public void Source_filter_keeps_lsp_copy_of_a_deduplicated_build_problem()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\src\A.cs(1,1): error CS1001: same message [C:\src\P.csproj]");
        vm.SetLspDiagnostics(new Uri(@"C:\src\A.cs").AbsoluteUri,
        [
            new(new(new(0, 0), new(0, 1)), "same message", DiagnosticSeverity.Error, "CS1001"),
        ]);

        vm.ShowBuild = false;

        var item = Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSource.Lsp, item.Source);
    }

    [Fact]
    public void Build_and_lsp_diagnostics_dedupe_even_when_severity_differs()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\src\A.cs(1,1): warning SA1600: same message [C:\src\P.csproj]");
        vm.SetLspDiagnostics(new Uri(@"C:\src\A.cs").AbsoluteUri,
        [
            new(new(new(0, 0), new(0, 1)), "same message", DiagnosticSeverity.Error, "StyleCop", "SA1600"),
        ]);

        var item = Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSource.Build, item.Source);
        Assert.Equal(ProblemSeverity.Warning, item.Severity);

        vm.ShowBuild = false;
        item = Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSource.Lsp, item.Source);
        Assert.Equal(ProblemSeverity.Error, item.Severity);
    }

    [Fact]
    public void Build_and_lsp_diagnostics_dedupe_when_localized_messages_differ()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\src\A.cs(1,1): warning SA1600: Build message [C:\src\P.csproj]");
        vm.SetLspDiagnostics(new Uri(@"C:\src\A.cs").AbsoluteUri,
        [
            new(new(new(0, 0), new(0, 1)), "LSP message", DiagnosticSeverity.Warning, "StyleCop", "SA1600"),
        ]);

        var item = Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSource.Build, item.Source);
        Assert.Equal("Build message", item.Message);

        vm.ShowBuild = false;
        item = Assert.Single(vm.Groups.SelectMany(g => g.Items));
        Assert.Equal(ProblemSource.Lsp, item.Source);
        Assert.Equal("LSP message", item.Message);
    }

    [Fact]
    public void Next_and_previous_wrap_in_visible_order()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(string.Join("\n",
            @"C:\src\A.cs(1,1): error CS1001: first [C:\src\P.csproj]",
            @"C:\src\A.cs(2,1): error CS1002: second [C:\src\P.csproj]"));
        var opened = new List<string>();
        vm.OpenRequested += item => opened.Add(item.Code);

        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.PreviousCommand.Execute(null);

        Assert.Equal(["CS1001", "CS1002", "CS1001", "CS1002"], opened);
    }

    [Fact]
    public void QuickFix_requests_editor_owned_code_actions_for_the_problem()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\src\A.cs(4,2): error CS1001: broken [C:\src\P.csproj]");
        ProblemItemViewModel? requested = null;
        vm.QuickFixRequested += item => requested = item;
        var item = Assert.Single(vm.Groups.SelectMany(g => g.Items));

        vm.QuickFixCommand.Execute(item);

        Assert.Same(item, requested);
    }

    [Fact]
    public void Thousand_diagnostics_replace_the_collection_with_one_property_notification()
    {
        var vm = new ProblemsViewModel();
        var groupsChanged = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Groups)) groupsChanged++; };
        var output = string.Join("\n", Enumerable.Range(1, 1000).Select(i =>
            $@"C:\src\File{i}.cs(1,1): error CS1001: problem {i} [C:\src\P.csproj]"));

        vm.SetFromBuildOutput(output);

        Assert.Equal(1000, vm.Groups.Count);
        Assert.Equal(1, groupsChanged);
    }

    /// <summary>コードを持たない LSP 診断は、Code に発生源名（"LSP" 等）が代入される。
    /// 重複排除の鍵を FilePath|行|列|Code だけにすると、同じ位置に出た別々の診断が
    /// 1 件に潰れて一覧から消える。メッセージまで見て区別すること。</summary>
    [Fact]
    public void Code_less_lsp_diagnostics_at_the_same_position_are_both_listed()
    {
        var vm = new ProblemsViewModel();
        var range = new LspRange(new LspPosition(3, 0), new LspPosition(3, 8));

        vm.SetLspDiagnostics("file:///C:/src/A.ts", [
            new LspDiagnostic(range, "未使用の変数です", DiagnosticSeverity.Warning),
            new LspDiagnostic(range, "この式は常に true です", DiagnosticSeverity.Warning),
        ]);

        var items = vm.Groups.SelectMany(g => g.Items).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Message == "未使用の変数です");
        Assert.Contains(items, i => i.Message == "この式は常に true です");
    }

    /// <summary>コードを持つ診断は従来どおり、同じ ID・同じ位置なら発生源をまたいで 1 件に畳む。</summary>
    [Fact]
    public void Same_code_at_the_same_position_is_still_deduplicated()
    {
        var vm = new ProblemsViewModel();
        var range = new LspRange(new LspPosition(3, 0), new LspPosition(3, 8));

        vm.SetLspDiagnostics("file:///C:/src/A.cs", [
            new LspDiagnostic(range, "; が必要です", DiagnosticSeverity.Error, "csharp", "CS1002"),
            new LspDiagnostic(range, "; expected", DiagnosticSeverity.Error, "csharp", "CS1002"),
        ]);

        Assert.Single(vm.Groups.SelectMany(g => g.Items));
    }
}
