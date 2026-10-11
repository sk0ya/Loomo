using System;
using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>テストファイルが属するプロジェクト（ランナーと、ランナーを走らせるディレクトリ）を決める。
/// <para><b>マルチルート</b>——上る範囲は、そのファイルを持つワークスペースフォルダー（<see cref="WorkspacePaths.FolderFor"/>）
/// まで。プライマリと前方一致で比べると、追加フォルダーのテストが「外」になるか、隣のフォルダーの設定を拾う
/// （CLAUDE.md「Multi-root workspaces」）。どのフォルダーにも属さないファイルは、従来どおり 6 段まで上る。</para>
/// <para><b>強い一致は最寄り、弱い一致は最も外側。</b>vitest/jest は package.json がパッケージの境界なので最寄りが正しい。
/// pytest は <c>tests/conftest.py</c> のような下位の手掛かりで rootdir を誤ると、ノード ID もモジュール名もずれるので、
/// 設定（pytest.ini / <c>[tool.pytest.ini_options]</c> 等）が無ければ最も外側の手掛かりを根にする。</para></summary>
public static class TestProjectLocator
{
    /// <summary>フォルダー外のファイルで上る段数（旧 <c>FindPackageDir</c> と同じ）。</summary>
    private const int MaxLevelsOutsideWorkspace = 6;

    /// <summary><paramref name="filePath"/> のテストを走らせるプロジェクト。見つからなければ null。
    /// 同じディレクトリで複数のランナーが一致したら <paramref name="runners"/> の並び順が勝つ。</summary>
    public static (string ProjectDir, ITestRunner Runner)? Find(string filePath, IReadOnlyList<string> folders,
        IReadOnlyList<ITestRunner> runners)
    {
        string? start;
        try { start = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
        catch { return null; }
        if (start is null) return null;

        (string, ITestRunner)? outermostWeak = null;
        foreach (var dir in Ancestors(start, folders))
        {
            foreach (var runner in runners)
            {
                switch (runner.Detect(dir))
                {
                    case TestRunnerMatch.Strong:
                        return (dir, runner);
                    case TestRunnerMatch.Weak:
                        outermostWeak = (dir, runner);
                        break;
                }
            }
        }
        return outermostWeak;
    }

    /// <summary><paramref name="startDir"/> から、それを持つワークスペースフォルダーまで（両端を含む）を内側から順に返す。</summary>
    public static IEnumerable<string> Ancestors(string startDir, IReadOnlyList<string> folders)
    {
        var owner = WorkspacePaths.FolderFor(folders, startDir);
        var ownerKey = owner is null ? null : WorkspacePaths.Normalize(owner);
        var dir = startDir;
        for (var level = 0; dir is not null; level++, dir = Path.GetDirectoryName(dir))
        {
            if (ownerKey is null && level >= MaxLevelsOutsideWorkspace) yield break;
            yield return dir;
            if (ownerKey is not null && string.Equals(WorkspacePaths.Normalize(dir), ownerKey,
                    StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }
}

/// <summary>ランナーの実行ファイルを探す（テストでは探し方を差し替える）。
/// <para>node は <c>node_modules/.bin</c> をプロジェクトからワークスペースフォルダーまで上って探す（モノレポの巻き上げ）。
/// Python は仮想環境（<c>.venv</c> / <c>venv</c> / <c>env</c>）→ PATH の順。PATH の解決は
/// <see cref="ExecutableResolver"/>——<c>.cmd</c> シムをフルパスで返すので、そのまま起動できる。</para></summary>
public sealed class TestToolLocator
{
    private static readonly string[] VenvDirs = [".venv", "venv", "env"];

    private readonly IReadOnlyList<string> _folders;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _resolveOnPath;

    public TestToolLocator(IReadOnlyList<string> folders, Func<string, bool>? fileExists = null,
        Func<string, string?>? resolveOnPath = null)
    {
        _folders = folders;
        _fileExists = fileExists ?? File.Exists;
        _resolveOnPath = resolveOnPath ?? ExecutableResolver.Resolve;
    }

    /// <summary><c>node_modules/.bin/&lt;name&gt;.cmd</c> のフルパス（無ければ null＝<c>npx</c> に任せる）。</summary>
    public string? FindNodeBin(string projectDir, string name)
    {
        foreach (var dir in TestProjectLocator.Ancestors(projectDir, _folders))
        {
            var candidate = Path.Combine(dir, "node_modules", ".bin", name + ".cmd");
            if (_fileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary><c>node_modules/&lt;relative&gt;</c>（例：<c>vitest/vitest.mjs</c>）のフルパス。<see cref="FindNodeBin"/> と
    /// 同じく上って探す（無ければ null）。デバッグではシムでなくランナー本体の JS を node で直に起動するため。</summary>
    public string? FindNodeModuleFile(string projectDir, string relative)
    {
        foreach (var dir in TestProjectLocator.Ancestors(projectDir, _folders))
        {
            var candidate = Path.Combine(dir, "node_modules", relative.Replace('/', Path.DirectorySeparatorChar));
            if (_fileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>プロジェクトの仮想環境の python.exe（無ければ null）。</summary>
    public string? FindVenvPython(string projectDir)
    {
        foreach (var dir in TestProjectLocator.Ancestors(projectDir, _folders))
            foreach (var venv in VenvDirs)
            {
                var candidate = Path.Combine(dir, venv, "Scripts", "python.exe");
                if (_fileExists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>PATH 上の実体のフルパス（無ければ null）。</summary>
    public string? ResolveOnPath(string executable) => _resolveOnPath(executable);
}
