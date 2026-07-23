using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークスペース内の package.json を検出し、npm スクリプトを読むヘルパ（TS IDE ペインの
/// パッケージ選択コンボ／npm スクリプトコンボ用）。<see cref="DebugProjectDiscovery"/> の TS 版で、
/// 候補の型は同じ <see cref="DebugProjectDiscovery.ProjectEntry"/> を使う（FullPath は package.json の絶対パス）。</summary>
public static class TsProjectDiscovery
{
    /// <summary>ワークスペース内の package.json を列挙する（肥大ディレクトリを飛ばした深さ制限走査）。
    /// Name は package.json の "name"（無ければディレクトリ名）。IsTest は常に false（npm に相当概念が無い）。</summary>
    public static IReadOnlyList<DebugProjectDiscovery.ProjectEntry> Discover(string root)
    {
        var found = new List<string>();
        CollectPackageJson(root, maxDepth: 3, found);
        var result = new List<DebugProjectDiscovery.ProjectEntry>();
        foreach (var path in found)
        {
            var name = ReadPackageName(path) ?? Path.GetFileName(Path.GetDirectoryName(path)!) ?? "package";
            var relative = Path.GetRelativePath(root, path);
            result.Add(new DebugProjectDiscovery.ProjectEntry(name, path, relative, IsTest: false));
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>package.json の scripts セクションのスクリプト名一覧（定義順）。読めなければ空。</summary>
    public static IReadOnlyList<string> ReadScripts(string packageJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();
            return scripts.EnumerateObject().Select(p => p.Name).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>npm モードの既定スクリプトを候補から選ぶ（dev → start → 先頭の優先順）。候補が空なら null。</summary>
    public static string? PickDefaultScript(IReadOnlyList<string> scripts)
        => scripts.FirstOrDefault(s => s == "dev")
        ?? scripts.FirstOrDefault(s => s == "start")
        ?? scripts.FirstOrDefault();

    private static string? ReadPackageName(string packageJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var v = name.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* 壊れた package.json はディレクトリ名で代替 */ }
        return null;
    }

    /// <summary>肥大ディレクトリ（node_modules/bin/obj/.git 等）を飛ばした深さ制限の package.json 走査。</summary>
    private static void CollectPackageJson(string dir, int maxDepth, List<string> found)
    {
        try
        {
            var pkg = Path.Combine(dir, "package.json");
            if (File.Exists(pkg)) found.Add(pkg);
            if (maxDepth <= 0) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "node_modules" or "bin" or "obj" or "dist" or "build" or ".git" or ".vs"
                    || name.StartsWith('.'))
                    continue;
                CollectPackageJson(sub, maxDepth - 1, found);
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
    }
}
