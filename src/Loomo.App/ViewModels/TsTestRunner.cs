using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>vitest / jest の実行と、その JSON 結果（jest 互換形式）のテスト一覧への反映をまとめたヘルパ。
/// dotnet 側の <see cref="DotnetTestRunner"/>（TRX）の TS 版。vitest は <c>--reporter=json</c>、jest は
/// <c>--json</c> が同じ形（testResults[].assertionResults[]）を出すので、パーサは 1 本で足りる。</summary>
internal static class TsTestRunner
{
    /// <summary>JSON 結果の出力先ディレクトリ。実行ごとに一意のファイルを作り、モノレポの
    /// パッケージ間や別セッションの結果が混ざらないようにする。</summary>
    private static readonly string ResultsDir = Path.Combine(Path.GetTempPath(), "Loomo", "test-results");

    /// <summary>結果 1 件。<see cref="Title"/> は describe 連結（"a &gt; b &gt; テスト名"、探索側と同じ形）。</summary>
    internal sealed record TsTestResult(string FilePath, string Title, TestStatus Status, string? Message);

    /// <summary>package.json からテストランナーを判定する（devDependencies / dependencies のキー、
    /// 次いで scripts.test の文言）。判定できなければ null。</summary>
    public static string? DetectRunner(string packageJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            foreach (var section in new[] { "devDependencies", "dependencies" })
            {
                if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
                if (deps.TryGetProperty("vitest", out _)) return "vitest";
                if (deps.TryGetProperty("jest", out _)) return "jest";
            }

            if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.String)
            {
                var cmd = test.GetString() ?? "";
                if (cmd.Contains("vitest", StringComparison.OrdinalIgnoreCase)) return "vitest";
                if (cmd.Contains("jest", StringComparison.OrdinalIgnoreCase)) return "jest";
            }
        }
        catch { /* 壊れた package.json は判定不能扱い */ }
        return null;
    }

    /// <summary>テストを実行し、生成された JSON 結果のパスを返す（生成されなければ null）。
    /// <paramref name="fileScope"/> はファイル限定実行（パッケージ相対パス）、<paramref name="testName"/> は
    /// <c>-t</c> による 1 件絞り込み（葉タイトル）。コマンドは PowerShell 経由なのでシングルクォートで包む。</summary>
    public static async Task<string?> RunAsync(ITerminalService terminal, IDebugSession session,
        string runner, string packageDir, string? fileScope, string? testName, string label)
    {
        var resultFile = Path.Combine(ResultsDir, $"loomo-ts-{Guid.NewGuid():N}.json");
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

        var command = BuildCommand(runner, packageDir, fileScope, testName, resultFile);
        session.Append(DebugOutputCategory.Important, label);
        var result = await terminal.RunCommandAsync(command, CancellationToken.None);
        session.WriteConsole(result.Output);
        if (!File.Exists(resultFile))
        {
            session.Append(DebugOutputCategory.Important,
                "テスト結果(JSON)が生成されませんでした。依存関係とテストランナーの出力を確認してください。");
            return null;
        }
        return resultFile;
    }

    /// <summary>読み終えた結果 JSON を消す（実行ごとに一意名で作るので、消さないと溜まり続ける）。</summary>
    public static void DeleteResults(string jsonPath)
    {
        try { File.Delete(jsonPath); }
        catch { /* 消せなくても実害は無い（次のスイープが拾う） */ }
    }

    /// <summary>取り残された古い結果 JSON を掃除する。アプリやテストランナーが落ちて
    /// <see cref="DeleteResults"/> まで届かなかったぶんが、ここで回収される。</summary>
    private static void SweepStaleResults()
    {
        try
        {
            var limit = DateTime.UtcNow.AddHours(-1);
            foreach (var f in Directory.EnumerateFiles(ResultsDir, "loomo-ts-*.json"))
                if (File.GetLastWriteTimeUtc(f) < limit)
                    try { File.Delete(f); } catch { /* 使用中なら次回 */ }
        }
        catch { /* 掃除に失敗しても実行は続ける */ }
    }

    /// <summary>実行コマンドを組み立てる（テスト用に分離）。パッケージディレクトリへ移動してから npx で実行する。</summary>
    internal static string BuildCommand(string runner, string packageDir, string? fileScope, string? testName)
        => BuildCommand(runner, packageDir, fileScope, testName,
            Path.Combine(ResultsDir, "loomo-ts-preview.json"));

    private static string BuildCommand(string runner, string packageDir, string? fileScope, string? testName,
        string resultFile)
    {
        var args = runner == "vitest"
            ? $"vitest run --reporter=json --outputFile={Q(resultFile)}"
            : $"jest --json --outputFile={Q(resultFile)}";
        if (fileScope is not null) args += $" {Q(fileScope.Replace('\\', '/'))}";
        if (testName is not null) args += $" -t {Q(testName)}";
        // --no-install は、タイプミスや依存未導入時にネットワークへ勝手に出ず、
        // 「この package の依存が足りない」という失敗をそのまま見せるために付ける。
        return $"Set-Location {Q(packageDir)}; npx --no-install {args}";
    }

    /// <summary>PowerShell のシングルクォート（内部の ' は '' に）。変数展開・バッククォートを無効化する。</summary>
    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>JSON 結果を読み、各結果を（ファイル×タイトル）で突き合わせて行のステータス・失敗メッセージを更新する。
    /// 一覧に無いテストは追加する（探索が拾えなかった動的テスト等）。</summary>
    public static void ApplyResults(string jsonPath, IDebugSession session,
        ObservableCollection<TestItemViewModel> tests, Func<string, string, TestItemViewModel> createItem)
    {
        List<TsTestResult> results;
        try
        {
            results = ParseJson(File.ReadAllText(jsonPath));
        }
        catch (Exception ex)
        {
            session.Append(DebugOutputCategory.Important, $"テスト結果(JSON)を読めません: {ex.Message}");
            return;
        }

        foreach (var r in results)
        {
            var fqn = MakeFqn(r.FilePath, r.Title);
            var item = tests.FirstOrDefault(t => string.Equals(t.FullyQualifiedName, fqn, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = createItem(r.FilePath, r.Title);
                tests.Add(item);
            }
            item.Update(r.Status, r.Message, sourcePath: null, line: 0);   // 位置は探索側の値を保持
        }
    }

    /// <summary>テスト行の同一性キー（正規化した絶対パス＋タイトル）。探索とランナーで同じ形を使う。</summary>
    public static string MakeFqn(string filePath, string title)
    {
        string full;
        try { full = Path.GetFullPath(filePath); } catch { full = filePath; }
        return full.Replace('/', '\\') + "::" + title;
    }

    /// <summary>jest 互換 JSON のパース（テスト用に分離）。testResults[].assertionResults[] から
    /// ancestorTitles + title を「a &gt; b &gt; タイトル」に組み立てる。</summary>
    internal static List<TsTestResult> ParseJson(string json)
    {
        var list = new List<TsTestResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("testResults", out var files) || files.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var file in files.EnumerateArray())
        {
            var path = file.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!file.TryGetProperty("assertionResults", out var asserts) || asserts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var a in asserts.EnumerateArray())
            {
                var title = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (a.TryGetProperty("ancestorTitles", out var anc) && anc.ValueKind == JsonValueKind.Array)
                {
                    var ancestors = anc.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                    if (ancestors.Count > 0) title = string.Join(" > ", ancestors) + " > " + title;
                }

                var status = a.TryGetProperty("status", out var st) ? st.GetString() : null;
                var mapped = status switch
                {
                    "passed" => TestStatus.Passed,
                    "failed" => TestStatus.Failed,
                    _ => TestStatus.Skipped,   // pending / skipped / todo / disabled
                };

                string? message = null;
                if (mapped == TestStatus.Failed &&
                    a.TryGetProperty("failureMessages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    var first = msgs.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        // 失敗メッセージは複数行（スタック付き）——1 行目だけを一覧の 2 行目に出す。
                        var text = first.GetString() ?? "";
                        var nl = text.IndexOf('\n');
                        message = (nl >= 0 ? text[..nl] : text).Trim();
                    }
                }

                list.Add(new TsTestResult(path, title, mapped, message));
            }
        }
        return list;
    }
}
