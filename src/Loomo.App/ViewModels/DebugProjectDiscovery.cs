using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークスペース内の .csproj を検出するヘルパ（起動プロジェクト選択コンボボックス用）。
/// <see cref="DebugTargetResolver"/> と同様 UI 非依存の静的クラス。<see cref="ProjectEntry"/> が
/// <c>DebugProfilesViewModel</c> の公開プロパティの型になるため、クラス自体も public。</summary>
public static class DebugProjectDiscovery
{
    /// <summary>検出した 1 プロジェクト。<see cref="FullPath"/> は .csproj の絶対パス、
    /// <see cref="RelativePath"/> はワークスペースルートからの相対パス（永続化のキーに使う）。
    /// <see cref="IsTest"/> はテストプロジェクトと判定できたか（一覧からの除外に使う）。</summary>
    public sealed record ProjectEntry(string Name, string FullPath, string RelativePath, bool IsTest);

    /// <summary>「自動検出」を表す実在のセンチネル項目（<c>null</c> は使わない）。WPF の <c>ComboBox.SelectedItem</c>
    /// バインディングは選択値が null だと ItemsSource 中の null 要素へは一致せず「未選択」扱いになり空欄表示に
    /// なってしまうため、必ずこの実体を選ぶ。<see cref="ProjectEntry.FullPath"/>/<see cref="ProjectEntry.RelativePath"/>
    /// が空文字列なのは、<c>DebugTargetResolver</c> 側の空文字＝自動検出の扱いとそのまま整合させるため。</summary>
    public static readonly ProjectEntry AutoDetect = new("(自動検出)", "", "", false);

    private static readonly Regex SlnProjectLine = new(
        @"^Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""(?<name>[^""]+)"",\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>ワークスペース内の .csproj を列挙し、テストプロジェクトを判別する。
    /// .sln があればその参照プロジェクトのみ（存在確認込み）、無ければ浅い再帰で全 .csproj を集める。
    /// 個々の読み取り失敗は無視（安全側で IsTest=false 扱い）。</summary>
    public static IReadOnlyList<ProjectEntry> Discover(string root)
    {
        var csprojPaths = FindCsprojPaths(root);
        var result = new List<ProjectEntry>();
        foreach (var path in csprojPaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var relative = Path.GetRelativePath(root, path);
            result.Add(new ProjectEntry(name, path, relative, IsTestProject(path)));
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> FindCsprojPaths(string root)
    {
        try
        {
            var sln = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (sln is not null)
            {
                var fromSln = ParseSlnProjects(sln);
                if (fromSln.Count > 0) return fromSln;
            }
        }
        catch { /* sln 読み取り失敗はディレクトリ走査へフォールバック */ }

        var found = new List<string>();
        CollectCsproj(root, maxDepth: 4, found);
        return found;
    }

    /// <summary>.sln の <c>Project("{guid}") = "Name", "path.csproj", "{guid}"</c> 行から .csproj 参照を読む。
    /// パスは .sln のディレクトリ基準で解決し、存在しないものは除外する。</summary>
    private static List<string> ParseSlnProjects(string slnPath)
    {
        var dir = Path.GetDirectoryName(slnPath)!;
        var result = new List<string>();
        foreach (Match m in SlnProjectLine.Matches(File.ReadAllText(slnPath)))
        {
            var relPath = m.Groups["path"].Value;
            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(Path.Combine(dir, relPath));
            if (File.Exists(full)) result.Add(full);
        }
        return result;
    }

    /// <summary>肥大ディレクトリ（bin/obj/.git/node_modules/.vs）を飛ばした深さ制限の .csproj 走査。</summary>
    private static void CollectCsproj(string dir, int maxDepth, List<string> found)
    {
        try
        {
            found.AddRange(Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly));
            if (maxDepth <= 0) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs" || name.StartsWith('.'))
                    continue;
                CollectCsproj(sub, maxDepth - 1, found);
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
    }

    /// <summary>csproj を軽く読み、テストプロジェクトかを判定する（<c>IsTestProject</c> プロパティ、または
    /// <c>Microsoft.NET.Test.Sdk</c> への PackageReference）。読み取り失敗時は false（実行対象候補として残す）。</summary>
    private static bool IsTestProject(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var isTestProp = doc.Descendants("IsTestProject")
                .Select(e => e.Value.Trim())
                .Any(v => bool.TryParse(v, out var b) && b);
            if (isTestProp) return true;

            return doc.Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? "")
                .Any(inc => inc.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
