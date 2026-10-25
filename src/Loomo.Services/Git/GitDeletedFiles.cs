using System;
using System.Collections.Generic;
using System.Globalization;

namespace sk0ya.Loomo.Services;

/// <summary>消したファイル 1 つ（§24.94）。<see cref="Hash"/> は消したコミット——消す直前の版はその親（<c>Hash^</c>）にある。</summary>
public sealed record GitDeletedFile(string Path, string Hash, string ShortHash, string Author, DateTimeOffset? Date, string Subject)
{
    /// <summary>消す直前の版（消したコミットの親）。</summary>
    public string RevisionBefore => Hash + "^";
}

/// <summary><c>git log --diff-filter=D --name-only</c> の出力を読む（純粋・§24.94）。</summary>
public static class GitDeletedFilesParser
{
    /// <summary>コミットの見出しの行の印（<c>%x1e</c>）と欄の区切り（<c>%x1f</c>）。</summary>
    public const string Format = "--format=%x1e%H%x1f%h%x1f%an%x1f%aI%x1f%s";

    /// <summary>
    /// 新しい順の出力から、パスごとに<b>いちばん新しく消したとき</b>だけを残す（同じパスを作り直してまた消したなら、最後の削除）。
    /// パスはリポジトリからの相対（<c>/</c> 区切り）。git が引用符で囲んだパス（制御文字など）は外す。
    /// </summary>
    public static IReadOnlyList<GitDeletedFile> Parse(string output)
    {
        var result = new List<GitDeletedFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[]? header = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('\u001e'))
            {
                var fields = line[1..].Split('\u001f');
                header = fields.Length >= 5 ? fields : null;
                continue;
            }
            if (header is null || line.Trim().Length == 0)
                continue;
            var path = Unquote(line.Trim());
            if (!seen.Add(path))
                continue;
            var date = DateTimeOffset.TryParse(header[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null;
            // 件名に区切りが入ることは無い（%x1f は git が件名に出さない）が、念のため残りを件名に寄せる。
            var subject = string.Join('\u001f', header[4..]);
            result.Add(new GitDeletedFile(path, header[0], header[1], header[2], date, subject));
        }
        return result;
    }

    private static string Unquote(string path)
        => path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\")
            : path;
}
