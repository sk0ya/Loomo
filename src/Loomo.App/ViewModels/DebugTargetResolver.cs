using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグ起動・手動ビルドの対象解決（プロジェクト探索・任意ビルド・出力 dll 探索）をまとめたヘルパ。
/// メッセージ出力・状態文言は <see cref="IDebugSession"/> 経由でコンソール/ヘッダへ流す。</summary>
internal static class DebugTargetResolver
{
    /// <summary>ワークスペースが C#/.NET プロジェクト（.sln/.csproj）を含むか。
    /// IDE（ビルド・テスト・デバッグ）ペインの表示要否判定に使う。root が null/空、または
    /// ビルド対象が 1 つも無ければ false。</summary>
    public static bool HasCSharpProject(string? root)
    {
        if (string.IsNullOrEmpty(root))
            return false;
        try
        {
            if (Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any())
                return true;
        }
        catch { /* 列挙失敗は csproj 探索へフォールバック */ }
        // この判定は起動時（初フレーム前）に同期実行されるため、FindProject の AllDirectories 全走査は使わない。
        // 非 C# の大きなフォルダ（node_modules 等）を開いたとき初フレームをブロックしないよう、bin/obj/.git/
        // node_modules といった肥大ディレクトリを除いた深さ制限スキャンで十分（プロジェクトは通常 src/ 直下までに在る）。
        return HasProjectWithinDepth(root, maxDepth: 3);
    }

    /// <summary>起動経路用の軽量 .csproj 判定。<paramref name="maxDepth"/> 段までを、肥大しがちな
    /// ビルド/依存/VCS ディレクトリを飛ばして探索する。1 つでも見つかれば true。</summary>
    private static bool HasProjectWithinDepth(string dir, int maxDepth)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                return true;
            if (maxDepth <= 0)
                return false;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs"
                    || name.StartsWith('.'))
                    continue;
                if (HasProjectWithinDepth(sub, maxDepth - 1))
                    return true;
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
        return false;
    }

    /// <summary>ビルド/テスト対象を解決する。ワークスペース直下の .sln を優先し、無ければ最初の .csproj。</summary>
    public static string? FindBuildTarget(IWorkspaceService workspace, IDebugSession session)
    {
        var root = workspace.RootPath;
        if (root is null)
        {
            session.Append(DebugOutputCategory.Important, "ワークスペースが開かれていません。");
            return null;
        }
        try
        {
            var sln = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly);
            if (sln.Length > 0) return sln[0];
        }
        catch { /* 列挙失敗時は csproj へフォールバック */ }

        var csproj = FindProject(root);
        if (csproj is null)
            session.Append(DebugOutputCategory.Important, "ワークスペースに .sln/.csproj が見つかりません。");
        return csproj;
    }

    /// <summary>デバッグ対象（実行する .dll）を解決する。明示指定が無ければワークスペースの .csproj を 1 つ探し、
    /// 任意でビルドしてから出力 dll を見つける。解決できなければ null（理由はコンソールへ）。</summary>
    public static async Task<string?> ResolveProgramAsync(IWorkspaceService workspace, ITerminalService terminal,
        IDebugSession session, string targetProgram, bool buildFirst)
    {
        var root = workspace.RootPath;

        // 明示指定があればそれを優先（相対はワークスペース基準）。
        if (!string.IsNullOrWhiteSpace(targetProgram))
        {
            var p = Path.IsPathRooted(targetProgram) || root is null
                ? targetProgram
                : Path.GetFullPath(Path.Combine(root, targetProgram));
            if (File.Exists(p)) return p;
            session.Append(DebugOutputCategory.Important, $"指定された実行対象が見つかりません: {p}");
            return null;
        }

        if (root is null)
        {
            session.Append(DebugOutputCategory.Important, "ワークスペースが開かれていません。デバッグ対象を指定してください。");
            return null;
        }

        var csproj = FindProject(root);
        if (csproj is null)
        {
            session.Append(DebugOutputCategory.Important,
                "ワークスペースに .csproj が見つかりません。デバッグ対象（.dll/.exe）を直接指定してください。");
            return null;
        }

        if (buildFirst && !await BuildAsync(terminal, session, csproj))
            return null;

        var dll = FindOutputDll(csproj);
        if (dll is null)
        {
            session.Append(DebugOutputCategory.Important,
                "ビルド出力 (.dll) が見つかりません。先にビルドするか、対象を直接指定してください。");
            return null;
        }
        return dll;
    }

    /// <summary><c>dotnet build</c> を実行し、出力をコンソールへ。成功（exit 0）なら true。</summary>
    public static async Task<bool> BuildAsync(ITerminalService terminal, IDebugSession session, string projectOrSln,
        string label = "ビルド")
    {
        session.StatusMessage = "ビルド中…";
        session.Append(DebugOutputCategory.Important, $"{label}: {Path.GetFileName(projectOrSln)}");
        var result = await terminal.RunCommandAsync(
            $"dotnet build \"{projectOrSln}\" -c Debug --nologo", CancellationToken.None);
        session.WriteConsole(result.Output);
        if (!result.Success)
        {
            session.StatusMessage = $"ビルド失敗（{result.ExitCode}）";
            session.Append(DebugOutputCategory.Important, $"ビルドに失敗しました（終了コード {result.ExitCode}）。");
            return false;
        }
        return true;
    }

    /// <summary>ワークスペース直下、無ければ浅い再帰で最初の .csproj を探す。</summary>
    public static string? FindProject(string root)
    {
        try
        {
            var top = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
            if (top.Length > 0) return top[0];
            return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>プロジェクトの <c>bin/Debug</c> 配下から <c>&lt;projName&gt;.dll</c> を新しい順に探す。</summary>
    public static string? FindOutputDll(string csproj)
    {
        try
        {
            var projDir = Path.GetDirectoryName(csproj)!;
            var name = Path.GetFileNameWithoutExtension(csproj);
            var binDir = Path.Combine(projDir, "bin", "Debug");
            if (!Directory.Exists(binDir)) return null;
            return Directory.EnumerateFiles(binDir, name + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
