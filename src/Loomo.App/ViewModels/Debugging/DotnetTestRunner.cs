using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.CSharp.Testing;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary><c>dotnet test</c>（TRX ロガー付き）の実行と、その結果のテスト一覧への反映をまとめたヘルパ。</summary>
internal static class DotnetTestRunner
{
    /// <summary><c>dotnet test</c> を TRX ロガー付きで実行する。<paramref name="filterExpr"/> が非 null なら
    /// <c>--filter</c> を付ける。出力はコンソールへ流し、生成された TRX のパスを返す（生成されなければ null）。</summary>
    public static async Task<CSharpTestExecutionResult?> RunAsync(ITerminalService terminal, IDebugSession session,
        string target, string? filterExpr, string label, string configuration = "Debug",
        string? targetFramework = null)
    {
        session.Append(DebugOutputCategory.Important, label);
        var execution = await CSharpTestExecutionService.RunAsync(
            terminal, target, filterExpr, configuration, CancellationToken.None, targetFramework);
        if (execution.PreparationError is { } preparationError)
        {
            session.Append(DebugOutputCategory.Important, preparationError);
            return execution;
        }
        if (execution.Command is not { } result) return execution;
        session.WriteConsole(result.Output);
        session.ReportBuildOutput(result.Output);  // dotnet test もビルドを含む——コンパイルエラーを「問題」へ
        return execution;
    }

    /// <summary><c>dotnet test</c>をcoverletのXPlat Code Coverage collector付きで実行し、結果フォルダーを返す。
    /// collector未導入の場合も、dotnet testの出力を通常どおりProblemsとコンソールへ流してnullを返す。</summary>
    public static async Task<(string Directory, int ExitCode)?> RunCoverageAsync(
        ITerminalService terminal, IDebugSession session, string target, string configuration = "Debug",
        string? targetFramework = null)
    {
        session.Append(DebugOutputCategory.Important, $"カバレッジ収集中: {Path.GetFileName(target)} ({configuration})");
        var execution = await CSharpTestExecutionService.RunCoverageAsync(
            terminal, target, configuration, CancellationToken.None, targetFramework);
        if (execution.PreparationError is { } preparationError)
        {
            session.Append(DebugOutputCategory.Important, preparationError);
            return null;
        }
        if (execution.Command is not { } result) return null;
        session.WriteConsole(result.Output);
        session.ReportBuildOutput(result.Output);
        return (execution.ResultsDirectory, result.ExitCode);
    }

    /// <summary>TRX を読み、各結果を名前で突き合わせて行のステータス・失敗メッセージ・ソース位置を更新する。
    /// テオリ等のケース（<c>FQN(args)</c>）は引数を落とした名前でメソッド単位の行へ集約する。一覧に無いテストは追加する。</summary>
    public static void ApplyTrx(string trxPath, IDebugSession session, ObservableCollection<TestItemViewModel> tests)
    {
        var results = CSharpTrxResultParser.Parse(trxPath, out var error);
        if (error is not null)
        {
            session.Append(DebugOutputCategory.Important, $"テスト結果(TRX)を読めません: {error}");
            return;
        }

        // 今回ぶんの合算をここで切る（テオリのケース所要時間。詳細は TestItemViewModel.BeginResultBatch）。
        foreach (var t in tests) t.BeginResultBatch();

        foreach (var r in results)
        {
            var item = tests.FirstOrDefault(t => string.Equals(t.FullyQualifiedName, r.Name, StringComparison.Ordinal));
            var isCase = false;
            if (item is null)
            {
                var paren = r.Name.IndexOf('(');
                if (paren > 0)
                {
                    var baseName = r.Name[..paren];
                    item = tests.FirstOrDefault(t => string.Equals(t.FilterExpression, baseName, StringComparison.Ordinal));
                    isCase = item is not null;
                }
            }
            if (item is null) { item = new TestItemViewModel(r.Name); tests.Add(item); }

            var status = r.Status switch
            {
                CSharpTestStatus.Passed => TestStatus.Passed,
                CSharpTestStatus.Failed => TestStatus.Failed,
                _ => TestStatus.Skipped,
            };
            if (isCase) item.ApplyCaseResult(status, r.Message, r.SourcePath, r.Line, r.Duration);
            else item.Update(status, r.Message, r.SourcePath, r.Line, r.Duration);
        }
    }
}
