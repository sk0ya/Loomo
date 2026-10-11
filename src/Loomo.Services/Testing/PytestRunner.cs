using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static sk0ya.Loomo.Services.Testing.PwshQuote;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>pytest：<c>python -m pytest --junitxml=… -o junit_family=xunit1 --rootdir=…</c>。
/// <para><b>rootdir を明示する</b>のは、JUnit の <c>classname</c>／<c>file</c> とノード ID がすべて rootdir 基準だから。
/// pytest 任せにすると ini の有無や引数で rootdir が動き、探索側の「ファイル::タイトル」と突き合わなくなる。</para>
/// <para>起動するのは仮想環境の python → PATH の pytest → <c>py</c> ランチャ → <c>python</c> の順。
/// 素の <c>python</c> を先に引かないのは、Windows の「アプリ実行エイリアス」（WindowsApps の python.exe）が
/// Store を開くだけのスタブで、PATH 上にあっても動かないことがあるため。</para></summary>
public sealed class PytestRunner : ITestRunner
{
    public string Id => "pytest";
    public string ResultFileExtension => ".xml";

    /// <summary>強い一致：pytest.ini／<c>[tool.pytest.ini_options]</c> を持つ pyproject.toml／<c>[tool:pytest]</c> を持つ
    /// setup.cfg／<c>[pytest]</c> を持つ tox.ini（＝pytest が rootdir に選ぶ設定ファイル）。
    /// 弱い一致：conftest.py・pyproject.toml・setup.py・setup.cfg、または pytest を挙げた requirements*.txt。
    /// 手掛かりの無い素のスクリプト置き場（.NET リポジトリの tools\test_x.py 等）は拾わない——Python の
    /// プロジェクトでない場所にテストタブを出さないため。</summary>
    public TestRunnerMatch? Detect(string directory)
    {
        try
        {
            if (File.Exists(Path.Combine(directory, "pytest.ini"))) return TestRunnerMatch.Strong;
            if (FileContains(directory, "pyproject.toml", "[tool.pytest")) return TestRunnerMatch.Strong;
            if (FileContains(directory, "setup.cfg", "[tool:pytest]")) return TestRunnerMatch.Strong;
            if (FileContains(directory, "tox.ini", "[pytest]")) return TestRunnerMatch.Strong;

            if (new[] { "conftest.py", "pyproject.toml", "setup.py", "setup.cfg" }
                    .Any(f => File.Exists(Path.Combine(directory, f))))
                return TestRunnerMatch.Weak;
            foreach (var req in Directory.EnumerateFiles(directory, "requirements*.txt"))
                if (File.ReadAllText(req).Contains("pytest", StringComparison.OrdinalIgnoreCase))
                    return TestRunnerMatch.Weak;
        }
        catch { /* 読めないディレクトリは判定不能扱い */ }
        return null;
    }

    private static bool FileContains(string directory, string fileName, string needle)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) && File.ReadAllText(path).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public string BuildCommand(TestRunRequest request, TestToolLocator tools)
    {
        var exe = tools.FindVenvPython(request.ProjectDir) is { } venv ? $"& {Q(venv)} -m pytest"
            : tools.ResolveOnPath("pytest") is { } pytest ? $"& {Q(pytest)}"
            : tools.ResolveOnPath("py") is { } py ? $"& {Q(py)} -m pytest"
            : "python -m pytest";
        var args = $"--junitxml={Q(request.ResultFile)} -o junit_family=xunit1 --rootdir={Q(request.ProjectDir)}";
        foreach (var target in request.EffectiveTargets)
        {
            // ノード ID（rootdir 相対・/ 区切り）。パラメータ化の 1 件は引数を落とした名前で全ケースを走らせる。
            var nodeId = target.File.Replace('\\', '/');
            if (target.Selector is { Length: > 0 } selector) nodeId += "::" + selector;
            args += $" {Q(nodeId)}";
        }
        return $"Set-Location {Q(request.ProjectDir)}; {exe} {args}";
    }

    public IReadOnlyList<ScriptTestResult> ParseResults(string content, string projectDir)
        => JUnitXmlResultParser.Parse(content, projectDir);
}
