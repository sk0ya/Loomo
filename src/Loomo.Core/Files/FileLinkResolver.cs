using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Files;

/// <summary>エディタ内リンクを既存ファイルまたはディレクトリへ解決する。</summary>
public static class FileLinkResolver
{
    private static readonly Regex TrailingLineColumn =
        new(@":(\d+)(?::(\d+))?$", RegexOptions.Compiled);

    /// <summary>
    /// ワークスペースの中でリンクを解決する。<b>ホストはこちらを使うこと。</b>
    ///
    /// <para>基準フォルダーは <paramref name="currentDocumentPath"/> から導くので、
    /// 「文書 A のリンクを文書 B の基準で解決する」という食い違いを書けない。
    /// 下の <c>baseFolder</c> 版は基準を呼ぶ側が決める素の関数で、そこにプライマリを
    /// 固定で渡していたのが §32.10.1 の不具合（追加フォルダーのファイルで
    /// ルート相対リンクが黙って外れる）だった。</para>
    /// </summary>
    public static bool TryResolve(
        IWorkspaceService workspace,
        string? target,
        string? currentDocumentPath,
        out string fullPath,
        out int line,
        out int column,
        out bool isDirectory)
        => TryResolve(
            target, currentDocumentPath, workspace.FolderForOrPrimary(currentDocumentPath),
            out fullPath, out line, out column, out isDirectory);

    /// <summary>基準フォルダーを明示して解決する素の関数。
    /// <paramref name="baseFolder"/> は相対リンクの最後の手がかり（文書のフォルダーで
    /// 見つからなかったときの基準）。ワークスペースを持てる場所では上の overload を使うこと。</summary>
    public static bool TryResolve(
        string? target,
        string? currentDocumentPath,
        string? baseFolder,
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

        foreach (var candidate in CandidatePaths(path, currentDocumentPath, baseFolder))
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

        path = path.Trim();
        // Markdown のリンク先では空白などが percent-encode される。
        // LinkDetector は "%20" を含むトークンを Path として検知するため、実在確認の直前に戻す。
        try { return Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return path; }
    }

    private static bool LooksLikeWindowsDrivePrefix(string path, Match match)
        => match.Index == 1 && path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]);

    private static IEnumerable<string> CandidatePaths(
        string path,
        string? currentDocumentPath,
        string? baseFolder)
    {
        if (Path.IsPathRooted(path))
        {
            yield return path;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            var currentDirectory = Directory.Exists(currentDocumentPath)
                ? currentDocumentPath
                : Path.GetDirectoryName(currentDocumentPath);
            if (!string.IsNullOrWhiteSpace(currentDirectory))
                yield return Path.Combine(currentDirectory, path);
        }

        if (!string.IsNullOrWhiteSpace(baseFolder))
            yield return Path.Combine(baseFolder, path);
    }
}
