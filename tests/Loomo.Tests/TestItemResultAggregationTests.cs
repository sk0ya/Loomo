using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.CSharp.Testing;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>テスト行への結果の反映（テオリのケース集約・所要時間の合算・探索マージ）の検証。
/// ガターのグリフとツールチップはここの結果をそのまま映すので、境界をここで固定する。</summary>
public class TestItemResultAggregationTests
{
    private static TestItemViewModel Item(string fqn = "N.C.Cases")
        => new(fqn) { IsParameterized = true };

    [Fact]
    public void Failed_filter_is_an_OR_of_unique_method_filters()
    {
        var failed = new[]
        {
            new TestItemViewModel("N.C.First"),
            new TestItemViewModel("N.C.Cases(1)"),
            new TestItemViewModel("N.C.Cases(2)"),
        };

        Assert.Equal("FullyQualifiedName=N.C.First|FullyQualifiedName=N.C.Cases",
            CSharpTestExecutionService.BuildFullyQualifiedNameFilter(
                failed.Select(test => test.FilterExpression)));
    }

    [Fact]
    public void File_filter_is_an_OR_of_unique_fully_qualified_test_methods()
    {
        var tests = new[]
        {
            new TestItemViewModel("N.C.First"),
            new TestItemViewModel("N.C.Cases(1)"),
            new TestItemViewModel("N.C.Cases(2)"),
        };

        Assert.Equal("FullyQualifiedName=N.C.First|FullyQualifiedName=N.C.Cases",
            CSharpTestExecutionService.BuildFullyQualifiedNameFilter(
                tests.Select(test => test.FilterExpression)));
    }

    [Fact]
    public void Discovery_metadata_is_retained_on_existing_test_rows()
    {
        var test = new TestItemViewModel("N.C.Cases");
        test.ApplyDiscoveryMetadata(true, "環境依存", ["優先度=高"]);

        Assert.True(test.IsParameterized);
        Assert.Equal("環境依存", test.SkipReason);
        Assert.Equal("優先度=高", test.TraitsText);
        Assert.True(test.HasSkipReason);
    }

    [Fact]
    public void Case_durations_are_summed_within_one_run()
    {
        var t = Item();
        t.SetRunning();
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(10));
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(20));
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(30));

        Assert.Equal(TestStatus.Passed, t.Status);
        Assert.Equal(60, t.Duration!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void A_new_batch_starts_the_sum_over_even_without_SetRunning()
    {
        // グループ実行の --filter は「表示中の行」より広いテストを拾うため、SetRunning されないまま
        // 結果だけ届く行がある。前回ぶんへ足し込むと所要時間が倍に見える。
        var t = Item();
        t.SetRunning();
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(10));
        Assert.Equal(10, t.Duration!.Value.TotalMilliseconds, 3);

        t.BeginResultBatch();
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(10));
        Assert.Equal(10, t.Duration!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void ResetStatus_also_clears_the_accumulation()
    {
        var t = Item();
        t.SetRunning();
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(10));
        t.ResetStatus();
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(10));

        Assert.Equal(10, t.Duration!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void One_failing_case_keeps_the_method_failed()
    {
        var t = Item();
        t.SetRunning();
        t.ApplyCaseResult(TestStatus.Failed, "boom", null, 0, TimeSpan.FromMilliseconds(5));
        t.ApplyCaseResult(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(5));

        Assert.Equal(TestStatus.Failed, t.Status);
        Assert.Equal("boom", t.Message);
        Assert.Equal(10, t.Duration!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void Update_replaces_the_duration_rather_than_adding_to_it()
    {
        var t = new TestItemViewModel("N.C.M");
        t.Update(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(30));
        t.Update(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(10));
        Assert.Equal(10, t.Duration!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void Declaration_path_is_normalized_once_when_set()
    {
        var t = new TestItemViewModel("N.C.M") { DeclarationPath = @"C:\work\sub\..\CTests.cs" };
        Assert.Equal(@"C:\work\CTests.cs", t.NormalizedDeclarationPath);

        t.DeclarationPath = null;
        Assert.Equal("", t.NormalizedDeclarationPath);
    }

    /// <summary>再走査で宣言位置（ガターの ▶ の置き場所）は毎回追従し、失敗位置（ジャンプ先）は
    /// 上書きされないこと。この 2 つを混ぜると「失敗したヘルパの行に ▶ が出る」に戻る。</summary>
    [Fact]
    public void Rediscovery_updates_the_declaration_position_but_keeps_the_failure_position()
    {
        var vm = TestExplorerFactory.CreateDotnetTests();
        vm.ApplyDiscovered([new DiscoveredTest("N.C.M", false, @"C:\work\CTests.cs", 10)]);

        var item = Assert.Single(vm.Tests);
        Assert.Equal(@"C:\work\CTests.cs", item.DeclarationPath);
        Assert.Equal(10, item.DeclarationLine);
        Assert.Null(item.SourcePath);   // 探索は失敗位置を触らない

        // 実行結果（スタックトレース）でジャンプ先だけが埋まる。
        item.Update(TestStatus.Failed, "boom", @"C:\work\Helpers.cs", 99);

        // 行がずれたので再走査。宣言位置は追従し、失敗位置はそのまま。
        vm.ApplyDiscovered([new DiscoveredTest("N.C.M", false, @"C:\work\CTests.cs", 14)]);

        Assert.Same(item, Assert.Single(vm.Tests));
        Assert.Equal(14, item.DeclarationLine);
        Assert.Equal(@"C:\work\Helpers.cs", item.SourcePath);
        Assert.Equal(99, item.Line);
        Assert.Equal(TestStatus.Failed, item.Status);
    }

    [Fact]
    public void Rediscovery_drops_unrun_tests_that_disappeared_but_keeps_ones_with_results()
    {
        var vm = TestExplorerFactory.CreateDotnetTests();
        vm.ApplyDiscovered([
            new DiscoveredTest("N.C.Gone", false, @"C:\work\CTests.cs", 5),
            new DiscoveredTest("N.C.Ran", false, @"C:\work\CTests.cs", 9),
        ]);
        vm.Tests.Single(t => t.FullyQualifiedName == "N.C.Ran").Update(TestStatus.Passed, null, null, 0);

        vm.ApplyDiscovered([]);

        Assert.Equal(["N.C.Ran"], vm.Tests.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void Authoritative_solution_rediscovery_drops_disappeared_tests_with_results()
    {
        var vm = TestExplorerFactory.CreateDotnetTests();
        vm.ApplyDiscovered([
            new DiscoveredTest("N.C.Gone", false, @"C:\work\CTests.cs", 5),
            new DiscoveredTest("N.C.Keep", false, @"C:\work\CTests.cs", 9),
        ]);
        vm.Tests.Single(t => t.FullyQualifiedName == "N.C.Gone")
            .Update(TestStatus.Failed, "old result", @"C:\work\CTests.cs", 5);
        vm.Tests.Single(t => t.FullyQualifiedName == "N.C.Keep")
            .Update(TestStatus.Passed, null, @"C:\work\CTests.cs", 9);

        vm.ApplyDiscovered([
            new DiscoveredTest("N.C.Keep", false, @"C:\work\CTests.cs", 10),
        ], authoritative: true);

        var remaining = Assert.Single(vm.Tests);
        Assert.Equal("N.C.Keep", remaining.FullyQualifiedName);
        Assert.Equal(TestStatus.Passed, remaining.Status);
        Assert.Equal(10, remaining.DeclarationLine);
    }

    [Fact]
    public void Solution_explorer_test_result_is_reflected_in_the_test_explorer()
    {
        var vm = TestExplorerFactory.CreateDotnetTests();
        vm.ApplyDiscovered([new DiscoveredTest("N.C.FromSolution", false,
            @"C:\work\Tests.cs", 12)]);
        var directory = Path.Combine(Path.GetTempPath(), "Loomo-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var trxPath = Path.Combine(directory, "loomo.trx");
        File.WriteAllText(trxPath, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="N.C.FromSolution" outcome="Passed" duration="00:00:00.0100000" />
              </Results>
            </TestRun>
            """);
        try
        {
            vm.ApplyExternalExecutionResult(new CSharpTestExecutionResult(null, trxPath));

            var item = Assert.Single(vm.Tests);
            Assert.Equal(TestStatus.Passed, item.Status);
            Assert.Contains("成功 1", vm.TestSummary, StringComparison.Ordinal);
            Assert.Single(vm.TestTree);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Trait_filter_shows_only_tests_with_the_requested_tag()
    {
        var vm = TestExplorerFactory.CreateDotnetTests();
        vm.ApplyDiscovered([
            new DiscoveredTest("N.C.Unit", false, Traits: ["Category=Unit"]),
            new DiscoveredTest("N.C.Integration", false, Traits: ["Category=Integration"]),
            new DiscoveredTest("N.C.Untagged", false),
        ]);

        vm.TraitFilter = "unit";

        var group = Assert.Single(vm.TestTree);
        Assert.Equal(["Unit"], group.Tests.Select(test => test.MethodName));
    }
}

/// <summary>テストから <see cref="DebugTestsViewModel"/> を組み立てる補助（副作用のある依存はフェイク、
/// ワークスペースは空＝自動収集が走らない）。</summary>
internal static class TestExplorerFactory
{
    public static DebugTestsViewModel CreateDotnetTests()
        => new DebugViewModel(
            new sk0ya.Loomo.Services.Debug.NetcoredbgDebugSessionFactory(),
            new FakeWorkspaceService(),
            new FakeTerminalService(),
            new sk0ya.Loomo.CSharp.Testing.TestDiscoveryService(),
            new DebugLaunchProfileStore(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}-launch.json"))).Tests;
}
