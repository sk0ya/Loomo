using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git blame --line-porcelain</c> の出力をパースする。<c>--porcelain</c> と違い、同じコミットが
/// 連続する行でもメタ情報（author/author-time 等）が毎行くり返されるため、直前の行の情報を持ち越す
/// 必要がなく単純に1行ずつ読める（1コミット分の情報は複数の物理行にまたがるが、その並びは固定）。
/// </summary>
public static class GitBlameParser
{
    /// <summary>1コミット分のヘッダー行に続くメタ情報の接頭辞。値は生の文字列のまま保持し、
    /// このパーサーで使うもの（author / author-time / author-tz）だけ取り出す。</summary>
    private const string AuthorPrefix = "author ";
    private const string AuthorTimePrefix = "author-time ";
    private const string AuthorTzPrefix = "author-tz ";

    public static IReadOnlyList<GitBlameLine> Parse(string output)
    {
        var lines = new List<GitBlameLine>();
        var raw = output.Replace("\r\n", "\n").Split('\n');

        var i = 0;
        while (i < raw.Length)
        {
            var header = raw[i];
            if (header.Length == 0)
            {
                i++;
                continue;
            }

            // ヘッダー行: "<hash> <元行番号> <現在の行番号>[ <グループ行数>]"
            var headerParts = header.Split(' ');
            if (headerParts.Length < 3
                || !int.TryParse(headerParts[1], out var originalLine)
                || !int.TryParse(headerParts[2], out var finalLine))
            {
                i++;
                continue; // 想定外の行は読み飛ばす（パニックにしない）
            }
            var hash = headerParts[0];
            i++;

            string? author = null;
            long authorTimeUnix = 0;
            var authorTz = "+0000";
            string? content = null;

            while (i < raw.Length)
            {
                var line = raw[i];
                if (line.StartsWith('\t'))
                {
                    content = line[1..]; // 先頭タブだけを外す。以降は生の行内容。
                    i++;
                    break;
                }

                if (line.StartsWith(AuthorPrefix, StringComparison.Ordinal))
                    author = line[AuthorPrefix.Length..];
                else if (line.StartsWith(AuthorTimePrefix, StringComparison.Ordinal))
                    long.TryParse(line[AuthorTimePrefix.Length..], out authorTimeUnix);
                else if (line.StartsWith(AuthorTzPrefix, StringComparison.Ordinal))
                    authorTz = line[AuthorTzPrefix.Length..];
                // summary / committer* / previous / filename / boundary は表示に使わないため無視する。
                i++;
            }

            if (content is null)
                break; // 出力がメタ情報の途中で終わっている（想定外の切れ方）。ここまでで打ち切る。

            var shortHash = hash.Length > 7 ? hash[..7] : hash;
            lines.Add(new GitBlameLine(
                hash, shortHash, author ?? "", FormatAuthorDate(authorTimeUnix, authorTz),
                originalLine, finalLine, content));
        }
        return lines;
    }

    /// <summary>author-time（UNIX秒・UTC）と author-tz（例: "+0900"）から著者ローカル時刻の表示文字列を作る。</summary>
    private static string FormatAuthorDate(long unixSeconds, string tz)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var local = utc.ToOffset(ParseTz(tz));
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private static TimeSpan ParseTz(string tz)
    {
        if (tz.Length == 5 && tz[0] is '+' or '-'
            && int.TryParse(tz.AsSpan(1, 2), out var hours)
            && int.TryParse(tz.AsSpan(3, 2), out var minutes))
        {
            var span = new TimeSpan(hours, minutes, 0);
            return tz[0] == '-' ? -span : span;
        }
        return TimeSpan.Zero;
    }
}
