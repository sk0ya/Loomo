using System;
using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Files;

/// <summary>
/// <see cref="SourceLocationParser"/> が読み取ったパスを、<b>実在するファイル</b>へ解決する。
///
/// <para><b>マルチルート。</b>ワークスペースは<b>フォルダーの集合</b>なので、プライマリだけを基準に
/// 相対パスを組み立てると、あとから追加したフォルダーの出力（ビルドエラー・スタックトレース）が
/// 黙って解決できなくなる。基準は次の順で試す:</para>
/// <list type="number">
/// <item>ターミナルの作業ディレクトリ（その出力を出した場所そのもの。あれば最優先）</item>
/// <item>選択元の文書があるフォルダー（エディタの右クリック）</item>
/// <item>その文書を担当するワークスペースフォルダー（<c>FolderForOrPrimary</c>）</item>
/// <item>ワークスペースの各フォルダー</item>
/// </list>
///
/// <para>実在しなければ false を返す。呼ぶ側（右クリックメニュー）は
/// <b>解決できたときだけ</b>項目を出すこと——押せるのに何も起きない項目を作らないため。</para>
/// </summary>
public static class SourceLocationResolver
{
    /// <summary>選択テキストを実在するファイルの位置へ解決する。</summary>
    /// <param name="workingDirectory">ターミナルの作業ディレクトリ（無ければ null）。</param>
    /// <param name="currentDocumentPath">選択元の文書のパス（無ければ null）。</param>
    public static bool TryResolve(
        IWorkspaceService workspace,
        string? selectedText,
        string? workingDirectory,
        string? currentDocumentPath,
        out SourceLocation location)
    {
        location = default;
        if (workspace is null || !SourceLocationParser.TryParse(selectedText, out var parsed))
            return false;

        foreach (var candidate in PathCandidates(parsed, selectedText))
        {
            foreach (var full in FullPaths(workspace, candidate.Path, workingDirectory, currentDocumentPath))
            {
                if (!File.Exists(full))
                    continue;
                location = candidate with { Path = full };
                return true;
            }
        }
        return false;
    }

    /// <summary>試すパスの候補。読み取ったパス → Git 接頭辞を剥がしたもの → 素の 1 行、の順。
    /// <b>剥がす前を先に試す</b>ので、<c>a/</c> で始まる実在フォルダーがあっても壊れない。
    /// 最後の「素の 1 行」は、行番号に見えたものが実はファイル名の一部だった場合
    /// （<c>foo(1).txt</c> など）の取りこぼしを拾う。</summary>
    private static IEnumerable<SourceLocation> PathCandidates(SourceLocation parsed, string? selectedText)
    {
        yield return parsed;

        if (SourceLocationParser.TryStripGitPrefix(parsed.Path, out var stripped))
            yield return parsed with { Path = stripped };

        var raw = SourceLocationParser.CleanFirstToken(selectedText);
        if (raw.Length > 0 && !string.Equals(raw, parsed.Path, StringComparison.Ordinal))
            yield return new SourceLocation(raw, 0, 0);
    }

    /// <summary>相対パスを組み立てる基準の順（クラスの説明を参照）。絶対パスならそれだけ。</summary>
    private static IEnumerable<string> FullPaths(
        IWorkspaceService workspace, string path, string? workingDirectory, string? currentDocumentPath)
    {
        foreach (var candidate in Combine(workspace, path, workingDirectory, currentDocumentPath))
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
            yield return full;
        }
    }

    private static IEnumerable<string> Combine(
        IWorkspaceService workspace, string path, string? workingDirectory, string? currentDocumentPath)
    {
        if (path.Length == 0)
            yield break;

        if (Path.IsPathRooted(path))
        {
            yield return path;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            yield return Path.Combine(workingDirectory, path);

        if (!string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            var directory = Directory.Exists(currentDocumentPath)
                ? currentDocumentPath
                : Path.GetDirectoryName(currentDocumentPath);
            if (!string.IsNullOrWhiteSpace(directory))
                yield return Path.Combine(directory, path);
        }

        // マルチルート: 文書の担当フォルダー（無ければプライマリ）→ 残りのフォルダー。
        if (workspace.FolderForOrPrimary(currentDocumentPath) is { Length: > 0 } owner)
            yield return Path.Combine(owner, path);

        foreach (var folder in workspace.Folders)
        {
            if (!string.IsNullOrWhiteSpace(folder))
                yield return Path.Combine(folder, path);
        }
    }
}
