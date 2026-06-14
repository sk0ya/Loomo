using System;
using System.IO;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

internal static class EditorFileLinkResolver
{
    private static readonly Regex TrailingLineColumn =
        new(@":(\d+)(?::(\d+))?$", RegexOptions.Compiled);

    public static bool TryResolve(
        string? target,
        string? currentDocumentPath,
        string? workspaceRoot,
        out string fullPath,
        out int line,
        out int column,
        out bool isDirectory)
    {
        fullPath = "";
        line = 0;
        column = 0;
        isDirectory = false;

        var path = CleanTarget(target);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var match = TrailingLineColumn.Match(path);
        if (match.Success && !LooksLikeWindowsDrivePrefix(path, match))
        {
            path = path[..match.Index];
            int.TryParse(match.Groups[1].Value, out line);
            if (match.Groups[2].Success)
                int.TryParse(match.Groups[2].Value, out column);
        }

        foreach (var candidate in CandidatePaths(path, currentDocumentPath, workspaceRoot))
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(full))
            {
                fullPath = full;
                return true;
            }

            if (Directory.Exists(full))
            {
                fullPath = full;
                isDirectory = true;
                return true;
            }
        }

        return false;
    }

    private static string CleanTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "";

        var path = target.Trim();
        if (path.Length >= 2 &&
            ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
            path = path[1..^1];

        return path.Trim();
    }

    private static bool LooksLikeWindowsDrivePrefix(string path, Match match)
        => match.Index == 1 && path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]);

    private static IEnumerable<string> CandidatePaths(
        string path,
        string? currentDocumentPath,
        string? workspaceRoot)
    {
        if (Path.IsPathRooted(path))
        {
            yield return path;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            var currentDir = Directory.Exists(currentDocumentPath)
                ? currentDocumentPath
                : Path.GetDirectoryName(currentDocumentPath);
            if (!string.IsNullOrWhiteSpace(currentDir))
                yield return Path.Combine(currentDir, path);
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            yield return Path.Combine(workspaceRoot, path);
    }
}
