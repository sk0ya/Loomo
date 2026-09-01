using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>C# Fix All の対象範囲。</summary>
public enum CSharpFixAllScope
{
    Document,
    Project,
    Solution,
}

/// <summary>Fix Allで解析するプロジェクトとCompileファイルの確定済みスナップショット。</summary>
public sealed record CSharpFixAllPlan(
    IReadOnlyList<ProjectModel> Projects,
    IReadOnlyList<string> Files,
    string? Error)
{
    public bool IsValid => Error is null && Projects.Count > 0 && Files.Count > 0;
}

/// <summary>SolutionModelからC# Fix Allの対象を決める。UIやLSPを知らないため、
/// context menuとcommand paletteが同じ対象集合を使える。</summary>
public static class CSharpFixAllPlanner
{
    /// <summary>Editorの「Fix all in file」用に、現在のC#文書だけを対象にする。</summary>
    public static CSharpFixAllPlan CreateForDocument(
        SolutionModel solution, string filePath)
    {
        ArgumentNullException.ThrowIfNull(solution);
        if (solution.State != ProjectLoadState.Ready)
            return Failure("C#ソリューションがまだ読み込まれていません。");

        var fullPath = Path.GetFullPath(filePath);
        if (!IsCSharpSource(fullPath) || !File.Exists(fullPath))
            return Failure("Fix Allの対象となるC#ファイルがありません。");
        var project = solution.ProjectForFile(fullPath);
        if (project is null)
            return Failure("対象のC#プロジェクトが見つかりません。");
        if (project.State != ProjectLoadState.Ready)
            return Failure("対象のC#プロジェクトがまだ読み込まれていません。");

        var isCompileFile = project.SelectedTargetFrameworkModel?.CompileFiles.Any(item =>
            string.Equals(Path.GetFullPath(item.FullPath), fullPath,
                StringComparison.OrdinalIgnoreCase)) == true;
        return isCompileFile
            ? new([project], [fullPath], null)
            : Failure("対象ファイルは選択中TargetFrameworkのCompile対象ではありません。");
    }

    public static CSharpFixAllPlan Create(
        SolutionModel solution, string projectPath, CSharpFixAllScope scope)
    {
        ArgumentNullException.ThrowIfNull(solution);
        if (solution.State != ProjectLoadState.Ready)
            return Failure("C#ソリューションがまだ読み込まれていません。");

        var fullProjectPath = Path.GetFullPath(projectPath);
        var project = solution.Projects.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FullPath), fullProjectPath,
                StringComparison.OrdinalIgnoreCase));
        if (project is null)
            return Failure("対象のC#プロジェクトが見つかりません。");
        if (project.State != ProjectLoadState.Ready)
            return Failure("対象のC#プロジェクトがまだ読み込まれていません。");

        if (scope == CSharpFixAllScope.Document)
            return Failure("Document範囲はCreateForDocumentを使用してください。");

        var projects = scope == CSharpFixAllScope.Solution
            ? solution.Projects.Where(candidate => candidate.State == ProjectLoadState.Ready).ToArray()
            : [project];
        var files = projects
            .SelectMany(candidate => candidate.SelectedTargetFrameworkModel?.CompileFiles ?? [])
            .Select(item => Path.GetFullPath(item.FullPath))
            .Where(IsCSharpSource)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return files.Length == 0
            ? Failure("Fix Allの対象となるC#ファイルがありません。")
            : new CSharpFixAllPlan(projects, files, null);
    }

    private static bool IsCSharpSource(string path)
        => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    private static CSharpFixAllPlan Failure(string error)
        => new(Array.Empty<ProjectModel>(), Array.Empty<string>(), error);
}

/// <summary>複数プロジェクトのFix All結果を、URI単位の競合を検査して統合する。
/// linked fileが異なる編集を返した場合は、部分適用を防ぐためエラーにする。</summary>
public static class CSharpFixAllEditMerger
{
    public static string? Merge(
        IDictionary<string, IReadOnlyList<LspTextEdit>> destination,
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> incoming)
    {
        // 先に全URIを検証する。検証と反映を同じループで行うと、後ろのURIで
        // 競合した際に前のURIだけがdestinationへ残り、Fix AllのWorkspaceEditが
        // 部分的に構築される。
        foreach (var (uri, edits) in incoming)
        {
            if (destination.TryGetValue(uri, out var existing)
                && !AreEqual(existing, edits))
                return $"linked file {uri} に異なる修正が返されました。";
        }

        foreach (var (uri, edits) in incoming)
            destination[uri] = edits;
        return null;
    }

    public static bool AreEqual(
        IReadOnlyList<LspTextEdit> left, IReadOnlyList<LspTextEdit> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Range.Equals(pair.Second.Range)
            && string.Equals(pair.First.NewText, pair.Second.NewText, StringComparison.Ordinal));
}
