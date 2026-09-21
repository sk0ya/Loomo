using Editor.Controls.Rendering;
using sk0ya.Loomo.CSharp.Testing;

namespace sk0ya.Loomo.App.Services;

/// <summary>カバレッジレポートをエディタの行マーカーへ変換する。</summary>
internal static class EditorCoverageMarkerMapper
{
    internal static IReadOnlyList<EditorCoverageMarker> ForPath(
        string? path, IReadOnlyList<CoverageFileSummary>? files)
    {
        var file = FindFile(path, files);
        if (file is null)
            return Array.Empty<EditorCoverageMarker>();

        var branchByLine = file.BranchDetails.ToDictionary(branch => branch.Line1);
        return file.LineDetails.Select(line =>
        {
            var kind = line.Covered
                ? CoverageMarkerKind.Covered
                : CoverageMarkerKind.Uncovered;
            var tooltip = line.Covered ? "カバレッジ: 実行済み" : "カバレッジ: 未実行";
            if (branchByLine.TryGetValue(line.Line1, out var branch))
            {
                kind = branch.CoveredBranches == 0
                    ? CoverageMarkerKind.Uncovered
                    : branch.CoveredBranches < branch.ValidBranches
                        ? CoverageMarkerKind.Partial
                        : kind;
                tooltip += $"／分岐 {branch.CoveredBranches}/{branch.ValidBranches}";
            }
            return new EditorCoverageMarker(line.Line1 - 1, kind, tooltip);
        }).ToArray();
    }

    private static CoverageFileSummary? FindFile(
        string? path, IReadOnlyList<CoverageFileSummary>? files)
    {
        if (string.IsNullOrWhiteSpace(path) || files is null)
            return null;

        var normalizedPath = Normalize(path);
        return files.FirstOrDefault(file =>
        {
            var candidate = Normalize(file.Path);
            return string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.EndsWith('/' + candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith('/' + normalizedPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
