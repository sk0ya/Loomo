using System.Xml.Linq;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>起動候補として検出したC#プロジェクト。UI型を参照せず、C#プロジェクト解析の結果だけを返す。</summary>
public sealed record CSharpProjectCandidate(
    string Name, string FullPath, string RelativePath, bool IsTest);

/// <summary>ワークスペース内のC#プロジェクトを検出する。sln／slnxの所属、csprojのテスト判定、
/// 深さ制限付きフォールバック走査をC#機能DLLへ閉じ込める。</summary>
public static class CSharpProjectDiscovery
{
    /// <summary>ワークスペース内の .csproj を列挙し、テストプロジェクトを判別する。</summary>
    public static IReadOnlyList<CSharpProjectCandidate> Discover(string root)
    {
        var csprojPaths = FindCsprojPaths(root);
        var result = new List<CSharpProjectCandidate>();
        foreach (var path in csprojPaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            result.Add(new CSharpProjectCandidate(
                name, path, Path.GetRelativePath(root, path), IsTestProject(path)));
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> FindCsprojPaths(string root)
    {
        try
        {
            return SolutionProjectDiscovery.Find(root).ProjectPaths;
        }
        catch { /* solution読み取り失敗はディレクトリ走査へフォールバック */ }

        var found = new List<string>();
        CollectCsproj(root, maxDepth: 8, found);
        return found;
    }

    private static void CollectCsproj(string directory, int maxDepth, List<string> found)
    {
        try
        {
            found.AddRange(Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly));
            if (maxDepth <= 0) return;
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs" || name.StartsWith('.'))
                    continue;
                CollectCsproj(sub, maxDepth - 1, found);
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
    }

    private static bool IsTestProject(string csprojPath)
    {
        try
        {
            var document = XDocument.Load(csprojPath);
            if (document.Descendants("IsTestProject")
                .Select(element => element.Value.Trim())
                .Any(value => bool.TryParse(value, out var result) && result)) return true;

            return document.Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include") ?? "")
                .Any(include => include.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
