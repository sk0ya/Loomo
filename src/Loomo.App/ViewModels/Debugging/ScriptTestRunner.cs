using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Testing;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary><see cref="ITestRunner"/>（vitest / jest / pytest）の 1 回の実行と、そのレポートのテスト一覧への反映。
/// dotnet 側の <see cref="DotnetTestRunner"/>（TRX）に当たる、ランナー非依存の版。
/// <para><b>実行は可視ターミナル</b>（<see cref="ITerminalService.RunCommandInVisibleTerminalAsync"/>）——dotnet test と同じく、
/// 人間が出力をその場で見られることを優先する。Loomo 側では子プロセスのパイプを読まないので、プールを塞ぐ読み
/// （§31.16 / <c>ChildProcessIo</c>）はここに生じない（可視ターミナルが無いときの退避先 <c>RunViaProcessAsync</c> は
/// <c>ChildProcessIo</c> で読む）。結果は標準出力を削るのではなく、ランナーに書かせた機械可読レポートから読む。</para></summary>
internal static class ScriptTestRunner
{
    /// <summary>レポートの出力先。実行ごとに一意のファイルを作り、モノレポのパッケージ間や別セッションの結果が混ざらないようにする。</summary>
    private static readonly string ResultsDir = Path.Combine(Path.GetTempPath(), "Loomo", "test-results");

    /// <summary>テストを実行し、生成されたレポートのパスを返す（生成されなければ null）。</summary>
    public static async Task<string?> RunAsync(ITerminalService terminal, IDebugSession session, ITestRunner runner,
        string projectDir, string? fileScope, string? testSelector, TestToolLocator tools, string label)
    {
        var resultFile = Path.Combine(ResultsDir, $"loomo-test-{Guid.NewGuid():N}{runner.ResultFileExtension}");
        try
        {
            Directory.CreateDirectory(ResultsDir);
            SweepStaleResults();
        }
        catch (Exception ex)
        {
            session.Append(DebugOutputCategory.Important, $"テスト結果フォルダを準備できません: {ex.Message}");
            return null;
        }

        var command = runner.BuildCommand(new TestRunRequest(projectDir, resultFile, fileScope, testSelector), tools);
        session.Append(DebugOutputCategory.Important, label);
        var result = await terminal.RunCommandInVisibleTerminalAsync(command, CancellationToken.None);
        session.WriteConsole(result.Output);
        if (!File.Exists(resultFile))
        {
            session.Append(DebugOutputCategory.Important,
                $"テスト結果（{runner.Id}）が生成されませんでした。依存関係とテストランナーの出力を確認してください。");
            return null;
        }
        return resultFile;
    }

    /// <summary>読み終えたレポートを消す（実行ごとに一意名で作るので、消さないと溜まり続ける）。</summary>
    public static void DeleteResults(string path)
    {
        try { File.Delete(path); }
        catch { /* 消せなくても実害は無い（次のスイープが拾う） */ }
    }

    /// <summary>取り残された古いレポートを掃除する。アプリやランナーが落ちて <see cref="DeleteResults"/> まで
    /// 届かなかったぶんが、ここで回収される（<c>loomo-ts-*</c> は旧 TsTestRunner の名前）。</summary>
    private static void SweepStaleResults()
    {
        try
        {
            var limit = DateTime.UtcNow.AddHours(-1);
            foreach (var pattern in new[] { "loomo-test-*", "loomo-ts-*.json" })
                foreach (var f in Directory.EnumerateFiles(ResultsDir, pattern))
                    if (File.GetLastWriteTimeUtc(f) < limit)
                        try { File.Delete(f); } catch { /* 使用中なら次回 */ }
        }
        catch { /* 掃除に失敗しても実行は続ける */ }
    }

    /// <summary>レポートを読み、各結果を（ファイル×タイトル）で突き合わせて行のステータス・失敗メッセージを更新する。
    /// 一覧に無いテストは追加する（探索が拾えなかった動的テスト等）。読めなければ false。</summary>
    public static bool ApplyResults(ITestRunner runner, string resultPath, string projectDir, IDebugSession session,
        ObservableCollection<TestItemViewModel> tests, Func<string, string, TestItemViewModel> createItem)
    {
        IReadOnlyList<ScriptTestResult> results;
        try
        {
            results = runner.ParseResults(File.ReadAllText(resultPath), projectDir);
        }
        catch (Exception ex)
        {
            session.Append(DebugOutputCategory.Important, $"テスト結果（{runner.Id}）を読めません: {ex.Message}");
            return false;
        }
        Apply(results, tests, createItem);
        return true;
    }

    /// <summary>結果の反映（純粋な突き合わせ部。単体テストから直接呼ぶ）。</summary>
    internal static void Apply(IReadOnlyList<ScriptTestResult> results, ObservableCollection<TestItemViewModel> tests,
        Func<string, string, TestItemViewModel> createItem)
    {
        // 今回ぶんの合算をここで切る（パラメータ化のケース所要時間。詳細は TestItemViewModel.BeginResultBatch）。
        foreach (var t in tests) t.BeginResultBatch();

        var byFqn = new Dictionary<string, TestItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tests) byFqn.TryAdd(t.FullyQualifiedName, t);

        foreach (var r in results)
        {
            var fqn = MakeFqn(r.FilePath, r.Title);
            if (!byFqn.TryGetValue(fqn, out var item))
            {
                item = createItem(r.FilePath, r.Title);
                if (r.DeclarationLine > 0)
                {
                    item.DeclarationPath = r.FilePath;
                    item.DeclarationLine = r.DeclarationLine;
                    item.SourcePath = r.FilePath;
                    item.Line = r.DeclarationLine;
                }
                if (r.CaseName is not null) item.IsParameterized = true;
                tests.Add(item);
                byFqn[fqn] = item;
            }

            var status = r.Outcome switch
            {
                ScriptTestOutcome.Passed => TestStatus.Passed,
                ScriptTestOutcome.Failed => TestStatus.Failed,
                _ => TestStatus.Skipped,
            };
            // 位置は失敗のトレースバックから取れたときだけ更新し、それ以外は探索側の値（宣言行）を保持する。
            if (r.CaseName is not null)
                item.ApplyCaseResult(status, r.Message, r.FailurePath, r.FailureLine, r.Duration);
            else
                item.Update(status, r.Message, r.FailurePath, r.FailureLine, r.Duration);
        }
    }

    /// <summary>テスト行の同一性キー（正規化した絶対パス＋タイトル）。探索とランナーで同じ形を使う。</summary>
    public static string MakeFqn(string filePath, string title)
    {
        string full;
        try { full = Path.GetFullPath(filePath); } catch { full = filePath; }
        return full.Replace('/', '\\') + "::" + title;
    }
}
