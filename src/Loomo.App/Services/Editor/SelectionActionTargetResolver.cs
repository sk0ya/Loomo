using Editor.Core.Text;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct MarkdownPathActionTarget(string LinkText, string SourcePath, bool IsDirectory);

/// <summary>エディタのキャレット位置にあるリンクを、選択アクションの対象へ解決する。</summary>
internal static class SelectionActionTargetResolver
{
    public static LinkOpenTarget OpenLinkAtCaret(
        IWorkspaceService workspace, string text, int line, int column, string? sourcePath)
    {
        if (!TryGetLineAtCaret(text, line, out var sourceLine))
            return LinkOpenTarget.None;
        if (LinkDetector.FindLinkAt(sourceLine, column) is not { } link)
            return LinkOpenTarget.None;
        return LinkOpenTargetResolver.Resolve(workspace, link.Text, sourcePath);
    }

    public static bool TryResolveMarkdownPathAtCaret(
        IWorkspaceService workspace, string text, int line, int column, string documentPath,
        out MarkdownPathActionTarget target)
    {
        target = default;
        if (!TryGetLineAtCaret(text, line, out var sourceLine)
            || LinkDetector.FindLinkAt(sourceLine, column) is not { Kind: LinkKind.FilePath } link
            || !FileLinkResolver.TryResolve(
                workspace, link.Text, documentPath,
                out var sourcePath, out _, out _, out var isDirectory))
            return false;

        target = new MarkdownPathActionTarget(link.Text, sourcePath, isDirectory);
        return true;
    }

    private static bool TryGetLineAtCaret(string text, int line, out string sourceLine)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line < 0 || line >= lines.Length)
        {
            sourceLine = "";
            return false;
        }

        sourceLine = lines[line];
        return true;
    }
}
