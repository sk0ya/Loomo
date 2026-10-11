using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using static sk0ya.Loomo.Services.Testing.PwshQuote;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>ディレクトリが vitest / jest のどちらのパッケージかを決める（両ランナーの <see cref="ITestRunner.Detect"/> の共通部）。
/// <para>優先順位は 1 本の関数に閉じ込めてある——ランナーごとに判定させると「vitest 依存＋jest.config がある」ような
/// 移行途中のパッケージで両方が名乗り出るため。順番：依存（devDependencies → dependencies、vitest が先）→
/// 設定ファイル（<c>vitest.config.*</c> / <c>vitest.workspace.*</c> → <c>jest.config.*</c>）→ package.json の
/// <c>"jest"</c> キー → <c>scripts.test</c> の文言。先頭の依存判定と末尾の scripts 判定は旧 <c>TsTestRunner.DetectRunner</c> と同じ。</para></summary>
public static class JsTestRunnerDetection
{
    private static readonly string[] ConfigExtensions = [".ts", ".mts", ".cts", ".js", ".mjs", ".cjs", ".json"];

    /// <summary><c>vitest</c> / <c>jest</c> / null。</summary>
    public static string? Detect(string directory)
    {
        var packageJson = Path.Combine(directory, "package.json");
        JsonDocument? doc = null;
        try
        {
            if (File.Exists(packageJson))
            {
                try { doc = JsonDocument.Parse(File.ReadAllText(packageJson)); }
                catch { /* 壊れた package.json は設定ファイルだけで判定する */ }
            }
            var root = doc?.RootElement is { ValueKind: JsonValueKind.Object } r ? r : (JsonElement?)null;

            if (root is { } deps0)
            {
                foreach (var section in new[] { "devDependencies", "dependencies" })
                {
                    if (!deps0.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
                    if (deps.TryGetProperty("vitest", out _)) return "vitest";
                    if (deps.TryGetProperty("jest", out _)) return "jest";
                }
            }

            if (HasConfig(directory, "vitest.config") || HasConfig(directory, "vitest.workspace")) return "vitest";
            if (HasConfig(directory, "jest.config")) return "jest";

            if (root is { } pkg)
            {
                if (pkg.TryGetProperty("jest", out var jestKey) && jestKey.ValueKind == JsonValueKind.Object) return "jest";
                if (pkg.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object &&
                    scripts.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.String)
                {
                    var cmd = test.GetString() ?? "";
                    if (cmd.Contains("vitest", StringComparison.OrdinalIgnoreCase)) return "vitest";
                    if (cmd.Contains("jest", StringComparison.OrdinalIgnoreCase)) return "jest";
                }
            }
        }
        catch { /* 読めないディレクトリは判定不能扱い */ }
        finally { doc?.Dispose(); }
        return null;
    }

    private static bool HasConfig(string directory, string stem)
        => ConfigExtensions.Any(ext => File.Exists(Path.Combine(directory, stem + ext)));

    /// <summary><c>-t</c>（テスト名パターン）に渡す文字列。vitest / jest とも<b>正規表現</b>として解釈するので、
    /// タイトルの <c>(</c> や <c>?</c> をそのまま渡すと別のテストに当たるか、構文エラーで 1 件も走らない。</summary>
    public static string EscapeNamePattern(string title)
    {
        var sb = new StringBuilder(title.Length + 8);
        foreach (var c in title)
        {
            if ("\\^$.|?*+()[]{}".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>実行対象を vitest / jest の引数の形へ分ける：ファイル（/ 区切り・重複なし）と <c>-t</c> の名前パターン。
    /// <c>-t</c> はファイルを問わず全体に効くので、ファイル全体を走らせる対象が 1 つでもあれば付けない（そのファイルの
    /// テストが削られないように）。複数の名前は選択（<c>(a|b)</c>）でまとめる——別ファイルの同名テストも走るが、
    /// 走りすぎは結果を突き合わせるだけで害が無く、走らなさすぎ（失敗のまま残って見える）よりよい。</summary>
    public static (IReadOnlyList<string> Files, string? NamePattern) SplitTargets(IReadOnlyList<TestTarget> targets)
    {
        var files = targets.Select(t => t.File.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0 || targets.Any(t => t.Selector is null)) return (files, null);
        var names = targets.Select(t => EscapeNamePattern(t.Selector!)).Distinct(StringComparer.Ordinal).ToList();
        return (files, names.Count == 1 ? names[0] : "(" + string.Join('|', names) + ")");
    }
}

/// <summary>vitest / jest の共通部：node_modules/.bin を優先し、無ければ <c>npx --no-install</c>。</summary>
public abstract class JsTestRunnerBase : ITestRunner
{
    public abstract string Id { get; }
    public string ResultFileExtension => ".json";

    public TestRunnerMatch? Detect(string directory)
        => string.Equals(JsTestRunnerDetection.Detect(directory), Id, StringComparison.Ordinal)
            ? TestRunnerMatch.Strong : null;

    public string BuildCommand(TestRunRequest request, TestToolLocator tools)
    {
        // パッケージの node_modules/.bin にあれば直接起動する（npx の解決を毎回挟まない）。無ければ npx に任せる。
        // --no-install は、タイプミスや依存未導入時にネットワークへ勝手に出ず、
        // 「この package の依存が足りない」という失敗をそのまま見せるために付ける。
        var exe = tools.FindNodeBin(request.ProjectDir, Id) is { } bin ? $"& {Q(bin)}" : $"npx --no-install {Id}";
        return $"Set-Location {Q(request.ProjectDir)}; {exe} {BuildArguments(request)}";
    }

    /// <summary>ランナー固有の引数（実行ファイルの後ろ）。</summary>
    protected abstract string BuildArguments(TestRunRequest request);

    public IReadOnlyList<ScriptTestResult> ParseResults(string content, string projectDir)
        => JestJsonResultParser.Parse(content);
}

/// <summary>vitest：<c>vitest run --reporter=json --outputFile=…</c>。ファイル指定は部分一致のフィルタ、<c>-t</c> は名前パターン。</summary>
public sealed class VitestRunner : JsTestRunnerBase
{
    public override string Id => "vitest";

    protected override string BuildArguments(TestRunRequest request)
    {
        var args = $"run --reporter=json --outputFile={Q(request.ResultFile)}";
        var (files, names) = JsTestRunnerDetection.SplitTargets(request.EffectiveTargets);
        foreach (var file in files) args += $" {Q(file)}";
        if (names is not null) args += $" -t {Q(names)}";
        return args;
    }
}

/// <summary>jest：<c>jest --json --outputFile=…</c>。ファイル指定は <c>--runTestsByPath</c>——jest の位置引数は
/// パスの<b>正規表現</b>なので、そのまま渡すと <c>.</c> や <c>(</c> を含むパスで別のファイルに当たる。</summary>
public sealed class JestRunner : JsTestRunnerBase
{
    public override string Id => "jest";

    protected override string BuildArguments(TestRunRequest request)
    {
        var args = $"--json --outputFile={Q(request.ResultFile)}";
        var (files, names) = JsTestRunnerDetection.SplitTargets(request.EffectiveTargets);
        if (files.Count > 0) args += " --runTestsByPath " + string.Join(' ', files.Select(Q));
        if (names is not null) args += $" -t {Q(names)}";
        return args;
    }
}
