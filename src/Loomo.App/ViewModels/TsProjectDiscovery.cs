using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>npm スクリプト 1 件（名前・実体コマンド・分類）。スクリプトタブの一覧表示用。
/// <see cref="Kind"/> は実体コマンドから <see cref="TsScriptClassifier"/> が判定する（起動手段の振り分け・種別バッジ）。</summary>
public sealed record TsScriptEntry(string Name, string Command, TsScriptKind Kind)
{
    /// <summary>種別バッジのラベル（スクリプトタブ表示用）。デバッグ対象外は空。</summary>
    public string KindBadge => Kind switch
    {
        TsScriptKind.FrontendDevServer => "ブラウザ",
        TsScriptKind.NodeRuntime => "Node",
        TsScriptKind.Test => "テスト",
        _ => "",
    };

    /// <summary>種別バッジを表示するか（Unknown / ビルド系は出さない）。</summary>
    public bool HasKindBadge => KindBadge.Length > 0;
}

/// <summary>ワークスペース内の package.json を検出し、npm スクリプトを読むヘルパ（TS IDE ペインの
/// パッケージ選択コンボ／スクリプトタブ一覧用）。<see cref="DebugProjectDiscovery"/> の TS 版で、
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
        => ReadScriptEntries(packageJsonPath).Select(e => e.Name).ToList();

    /// <summary>package.json の scripts セクション（名前＋実体コマンド。定義順）。読めなければ空。</summary>
    public static IReadOnlyList<TsScriptEntry> ReadScriptEntries(string packageJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object)
                return Array.Empty<TsScriptEntry>();
            return scripts.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p =>
                {
                    var cmd = p.Value.GetString() ?? "";
                    return new TsScriptEntry(p.Name, cmd, TsScriptClassifier.Classify(cmd));
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<TsScriptEntry>();
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
