using Editor.Core.Text;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>移動したファイルを指す Markdown リンクを本文内で書き換える。</summary>
internal static class MarkdownLocalLinkTextRewriter
{
    public static string ReplaceReferences(
        string text, IWorkspaceService workspace, string documentPath, string sourcePath, string destination)
    {
        var replacements = new List<(int Start, int Length)>();
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            foreach (var link in LinkDetector.FindLinks(line))
            {
                if (link.Kind == LinkKind.FilePath
                    && FileLinkResolver.TryResolve(
                        workspace, link.Text, documentPath,
                        out var resolved, out _, out _, out _)
                    && PathsEqual(resolved, sourcePath))
                    replacements.Add((offset + link.Start, link.End - link.Start));
            }
            offset += line.Length + 1;
        }
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            text = text.Remove(replacement.Start, replacement.Length)
                .Insert(replacement.Start, destination);
        return text;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(WorkspacePaths.Normalize(left), WorkspacePaths.Normalize(right),
            StringComparison.OrdinalIgnoreCase);
}
