using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TypeScript / Node.js デバッグの対象解決ヘルパ。<see cref="DebugTargetResolver"/> の TS 版で、
/// ペイン表示のゲート（<see cref="HasTypeScriptProject"/>）と tsc 型チェックの対象ディレクトリ解決を担う。</summary>
internal static class TsDebugTargetResolver
{
    /// <summary>ワークスペースが TypeScript / Node.js プロジェクト（tsconfig.json / package.json）を含むか。
    /// TS IDE ペインの表示要否判定に使う。起動時（初フレーム前）に同期実行されるため、dotnet 側の
    /// <c>HasCSharpProject</c> と同じ「肥大ディレクトリを飛ばした深さ制限スキャン」で軽量に判定する。</summary>
    public static bool HasTypeScriptProject(IReadOnlyList<string> folders)
        => folders.Any(HasTypeScriptProjectIn);

    private static bool HasTypeScriptProjectIn(string? root)
    {
        if (string.IsNullOrEmpty(root))
            return false;
        return HasMarkerWithinDepth(root, maxDepth: 3);
    }

    private static bool HasMarkerWithinDepth(string dir, int maxDepth)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "tsconfig.json")) || File.Exists(Path.Combine(dir, "package.json")))
                return true;
            if (maxDepth <= 0)
                return false;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "node_modules" or "bin" or "obj" or "dist" or "build" or ".git" or ".vs"
                    || name.StartsWith('.'))
                    continue;
                if (HasMarkerWithinDepth(sub, maxDepth - 1))
                    return true;
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
        return false;
    }

    /// <summary>tsc 型チェック（<c>npx tsc --noEmit</c>）の対象ディレクトリ＝tsconfig.json のあるディレクトリを
    /// 探す。<paramref name="preferredDir"/>（選択中パッケージのディレクトリ）を優先し、無ければ各ワークスペース
    /// フォルダーを浅くスキャンして最初の tsconfig.json を返す。見つからなければ null。</summary>
    public static string? FindTsconfigDir(IReadOnlyList<string> folders, string? preferredDir = null)
    {
        if (preferredDir is not null && File.Exists(Path.Combine(preferredDir, "tsconfig.json")))
            return preferredDir;

        foreach (var root in folders)
        {
            var found = FindTsconfigDirWithinDepth(root, maxDepth: 3);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? FindTsconfigDirWithinDepth(string dir, int maxDepth)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "tsconfig.json")))
                return dir;
            if (maxDepth <= 0)
                return null;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "node_modules" or "bin" or "obj" or "dist" or "build" or ".git" or ".vs"
                    || name.StartsWith('.'))
                    continue;
                if (FindTsconfigDirWithinDepth(sub, maxDepth - 1) is { } found)
                    return found;
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
        return null;
    }
}
