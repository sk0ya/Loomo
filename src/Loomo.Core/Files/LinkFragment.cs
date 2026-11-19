using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Files;

/// <summary>
/// リンクの <c>#…</c>（フラグメント）を、開くファイルの行へ読み替える。
/// <list type="bullet">
/// <item><c>#L12</c>・<c>#L12-L20</c>（GitHub の行の印）→ 12 行目。どのファイルでも。</item>
/// <item>Markdown の <c>#見出し</c> → その見出しの行。照合はプレビューの見出しの id（小文字にして空白を - に。
///   プレビューの見出しの「#」でコピーされる形）と、GitHub 式の slug（記号を落とす）の両方。</item>
/// </list>
/// 分からなければ 0（行の指定なし＝ファイルを開くだけ）。
/// </summary>
public static class LinkFragment
{
    private static readonly Regex LineAnchor = new(@"^L(\d+)(?:-L?\d+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);

    /// <summary>見出しを探すために読む Markdown の上限。これより大きいファイルは開くだけにする（クリックで止めない）。</summary>
    private const long MaxMarkdownBytes = 4 * 1024 * 1024;

    /// <summary>1 始まりの行。分からなければ 0。</summary>
    public static int LineOf(string filePath, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
            return 0;
        fragment = fragment.Trim();

        var lineAnchor = LineAnchor.Match(fragment);
        if (lineAnchor.Success && int.TryParse(lineAnchor.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;

        if (!IsMarkdown(filePath))
            return 0;
        try
        {
            if (new FileInfo(filePath).Length > MaxMarkdownBytes)
                return 0;
            return HeadingLineOf(File.ReadAllLines(filePath), fragment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>本文の行から、フラグメントに合う見出しの行（1 始まり）。コードブロックの中の # は見出しにしない。</summary>
    public static int HeadingLineOf(string[] lines, string fragment)
    {
        var wanted = fragment.Trim().TrimStart('#');
        if (wanted.Length == 0)
            return 0;

        var slugLine = 0;
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;
            var m = HeadingLine.Match(lines[i]);
            if (!m.Success)
                continue;
            var text = m.Groups[2].Value.Trim();
            // プレビューと同じ id（MarkdownRenderer.HeadingId）を先に。合えばそこで決まり。
            if (string.Equals(PreviewId(text), wanted, StringComparison.Ordinal))
                return i + 1;
            if (slugLine == 0 && string.Equals(GitHubSlug(text), GitHubSlug(wanted), StringComparison.Ordinal))
                slugLine = i + 1;
        }
        return slugLine;
    }

    /// <summary>プレビューが見出しに付ける id（HTML の実体参照を戻した形）。</summary>
    public static string PreviewId(string headingText) => headingText.ToLowerInvariant().Replace(' ', '-');

    /// <summary>GitHub 式：小文字・文字と数字と - _ だけ残す・空白は -。日本語の文字はそのまま残る。</summary>
    public static string GitHubSlug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
        }
        return sb.ToString();
    }

    private static bool IsMarkdown(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdx" or ".mdown";
}
