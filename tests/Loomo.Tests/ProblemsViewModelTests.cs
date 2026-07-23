using sk0ya.Loomo.App.ViewModels;
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
    public void SetFromBuildOutput_orders_errors_first_and_counts()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(string.Join("\n",
            @"C:\src\B.cs(3,1): warning CS0219: 未使用 [C:\src\P.csproj]",
            @"C:\src\A.cs(9,1): error CS1002: ; が必要です [C:\src\P.csproj]"));

        Assert.True(vm.HasItems);
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);
        Assert.Equal(ProblemSeverity.Error, vm.Items[0].Severity);   // エラーが先頭

        // きれいなビルド出力で空に戻る。
        vm.SetFromBuildOutput("    0 個の警告\n    0 エラー");
        Assert.False(vm.HasItems);
        Assert.Empty(vm.Items);
    }
}
