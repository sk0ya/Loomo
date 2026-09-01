using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Projects;

public enum CSharpWorkspaceSourceScope
{
    ProjectGraph,
    Solution,
}

public sealed record CSharpWorkspaceSourceSnapshot(
    IReadOnlyDictionary<string, string> Texts,
    IReadOnlyDictionary<string, CSharpParseOptions> ParseOptionsByPath,
    int SkippedFileCount = 0)
{
    /// <summary>上限超過によるファイル欠落がなく、Compilationが全対象を含む状態か。</summary>
    public bool IsComplete => SkippedFileCount == 0;
}

/// <summary>Roslyn構文fallbackが参照型を解決するためのC#ソーススナップショットを作る。
/// 選択プロジェクトとProjectReference先だけを辿り、巨大ファイルや未読込プロジェクトは読み飛ばす。
/// 現在のEditor本文は最後に上書きし、未保存内容を常に正本にする。<c>openTexts</c>を渡した場合は、
/// アクティブ文書以外の開いているCompileファイルも同じように未保存本文を優先する。</summary>
public static class CSharpWorkspaceSourceLoader
{
    // 大規模solutionでは、1ファイルが小さくても全ファイルをCompilationへ積むと
    // 編集操作ごとのGCとRoslynのメモリ使用量が膨らむ。アクティブ文書はこの上限に
    // 関係なく最後に未保存本文で上書きするため、現在編集中の診断を失わない。
    internal const int MaxSourceFileCount = 4096;
    private const long MaxSourceBytes = 8 * 1024 * 1024;
    private const long MaxSnapshotBytes = 64 * 1024 * 1024;

    /// <summary>アクティブ文書と同じ選択TFMの条件付きコンパイル記号／言語バージョンを返す。</summary>
    public static CSharpParseOptions ParseOptionsForFile(SolutionModel? solution, string activePath)
        => CSharpProjectCompilationOptions.Parse(
            solution?.ProjectForFile(Path.GetFullPath(activePath))?.SelectedTargetFrameworkModel);

    public static IReadOnlyDictionary<string, string> Load(
        SolutionModel? solution,
        string activePath,
        string activeText,
        CSharpWorkspaceSourceScope scope = CSharpWorkspaceSourceScope.ProjectGraph,
        IReadOnlyDictionary<string, string>? openTexts = null)
        => LoadSnapshot(solution, activePath, activeText, scope, openTexts).Texts;

    public static CSharpWorkspaceSourceSnapshot LoadSnapshot(
        SolutionModel? solution,
        string activePath,
        string activeText,
        CSharpWorkspaceSourceScope scope = CSharpWorkspaceSourceScope.ProjectGraph,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        var activeFullPath = Path.GetFullPath(activePath);
        var normalizedOpenTexts = NormalizeOpenTexts(openTexts);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parseOptionsByPath = new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase);
        var budget = new SourceLoadBudget(MaxSourceFileCount, MaxSnapshotBytes);
        var skippedFileCount = 0;
        var projects = (solution?.Projects ?? [])
            .Where(project => project.State == ProjectLoadState.Ready)
            .ToDictionary(project => Path.GetFullPath(project.FullPath),
                StringComparer.OrdinalIgnoreCase);
        var start = solution?.ProjectForFile(activeFullPath);
        var queue = new Queue<ProjectModel>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scope == CSharpWorkspaceSourceScope.Solution)
        {
            // linked fileがsolution内の複数projectに含まれる場合、pathだけをキーにする
            // snapshotでは一つのParseOptionsしか保持できない。active documentの担当
            // projectを先に処理し、そのprojectの条件付きコンパイル設定を優先する。
            if (start is not null) queue.Enqueue(start);
            foreach (var project in projects.Values)
                if (start is null || !string.Equals(
                        Path.GetFullPath(project.FullPath), Path.GetFullPath(start.FullPath),
                        StringComparison.OrdinalIgnoreCase))
                    queue.Enqueue(project);
        }
        else if (start is not null)
        {
            queue.Enqueue(start);
        }

        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            var projectPath = Path.GetFullPath(project.FullPath);
            if (!visited.Add(projectPath)) continue;
            var parseOptions = CSharpProjectCompilationOptions.Parse(
                project.SelectedTargetFrameworkModel);

            foreach (var item in project.SelectedTargetFrameworkModel?.CompileFiles ?? [])
            {
                if (!string.Equals(Path.GetExtension(item.FullPath), ".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryRead(result, parseOptionsByPath, item.FullPath, parseOptions,
                        budget, normalizedOpenTexts) == SourceLoadResult.SkippedByBudget
                    && !string.Equals(Path.GetFullPath(item.FullPath), activeFullPath,
                        StringComparison.OrdinalIgnoreCase))
                    skippedFileCount++;
            }

            var projectReferences = project.SelectedTargetFrameworkModel?.ProjectReferences
                ?? project.ProjectReferences;
            foreach (var reference in projectReferences)
                if (projects.TryGetValue(Path.GetFullPath(reference), out var referenced))
                    queue.Enqueue(referenced);
        }

        result[activeFullPath] = activeText;
        parseOptionsByPath[activeFullPath] = CSharpProjectCompilationOptions.Parse(
            start?.SelectedTargetFrameworkModel);
        return new CSharpWorkspaceSourceSnapshot(result, parseOptionsByPath, skippedFileCount);
    }

    private static IReadOnlyDictionary<string, string>? NormalizeOpenTexts(
        IReadOnlyDictionary<string, string>? openTexts)
    {
        if (openTexts is null || openTexts.Count == 0) return null;
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in openTexts)
        {
            if (string.IsNullOrWhiteSpace(path) || text is null) continue;
            try { normalized[Path.GetFullPath(path)] = text; }
            catch (ArgumentException) { }
        }
        return normalized.Count == 0 ? null : normalized;
    }

    private static SourceLoadResult TryRead(
        IDictionary<string, string> result,
        IDictionary<string, CSharpParseOptions> parseOptionsByPath,
        string path,
        CSharpParseOptions parseOptions,
        SourceLoadBudget budget,
        IReadOnlyDictionary<string, string>? openTexts)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (result.ContainsKey(fullPath)) return SourceLoadResult.Loaded;
            string? openText = null;
            var hasOpenText = openTexts is not null && openTexts.TryGetValue(fullPath, out openText);
            if (!hasOpenText && !File.Exists(fullPath)) return SourceLoadResult.NotLoaded;
            var length = hasOpenText
                ? System.Text.Encoding.UTF8.GetByteCount(openText!)
                : new FileInfo(fullPath).Length;
            if (length > MaxSourceBytes || !budget.TryReserve(fullPath, length))
                return SourceLoadResult.SkippedByBudget;
            try
            {
                result[fullPath] = hasOpenText ? openText! : File.ReadAllText(fullPath);
                parseOptionsByPath[fullPath] = parseOptions;
                return SourceLoadResult.Loaded;
            }
            catch
            {
                budget.Release(fullPath, length);
                throw;
            }
        }
        catch (IOException) { return SourceLoadResult.NotLoaded; }
        catch (UnauthorizedAccessException) { return SourceLoadResult.NotLoaded; }
        catch (ArgumentException) { return SourceLoadResult.NotLoaded; }
    }

    private enum SourceLoadResult
    {
        NotLoaded,
        Loaded,
        SkippedByBudget,
    }

    private sealed class SourceLoadBudget(int maxFiles, long maxBytes)
    {
        private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
        private int _fileCount;
        private long _byteCount;

        public bool TryReserve(string path, long length)
        {
            if (length < 0 || _fileCount >= maxFiles || _byteCount > maxBytes - length)
                return false;
            if (!_reserved.Add(path)) return true;
            _fileCount++;
            _byteCount += length;
            return true;
        }

        public void Release(string path, long length)
        {
            if (_reserved.Remove(path))
            {
                _fileCount--;
                _byteCount -= length;
            }
        }
    }
}
