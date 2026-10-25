using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>パッチの1行の種類。</summary>
public enum PatchLineKind { Context, Added, Removed, NoNewline }

/// <summary>パッチの1行。行番号はその側に行が無ければ null。</summary>
public sealed record PatchLine(PatchLineKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>ハンク1つ（<c>@@ -a,b +c,d @@ 見出し</c> と行）。</summary>
public sealed record PatchHunk(string Header, IReadOnlyList<PatchLine> Lines);

/// <summary>パッチの中のファイル1つ。<see cref="OldPath"/>／<see cref="NewPath"/> は追加・削除なら片方が null。</summary>
public sealed record PatchFile(
    string? OldPath, string? NewPath, IReadOnlyList<string> Meta, IReadOnlyList<PatchHunk> Hunks, bool IsBinary)
{
    /// <summary>一覧に出す名前（改名なら「旧 → 新」）。</summary>
    public string DisplayPath => (OldPath, NewPath) switch
    {
        ({ } o, { } n) when o != n => $"{o} → {n}",
        (_, { } n) => n,
        ({ } o, null) => o,
        _ => "(名前なし)",
    };

    public int Added { get; init; }
    public int Removed { get; init; }
}

/// <summary>
/// 統合形式のパッチ（<c>git diff</c>／<c>git format-patch</c>／<c>diff -u</c> の出力）をファイル・ハンク・行に割る（純ロジック・テスト対象）。§24.68。
/// <para>読むためのもので、当てるためのものではない（当てるのは git apply・§24.38.1）。壊れたところは捨てずに読める範囲で読む——
/// ハンクの行数が見出しと合わなくても、次の見出しかファイルの区切りまでをそのハンクとして扱う。メールの前置き（format-patch の
/// From:／Subject:）や最初の <c>diff</c> より前の文は <see cref="Preamble"/> に残す。</para>
/// </summary>
public sealed class PatchDocument
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$", RegexOptions.CultureInvariant);

    public IReadOnlyList<string> Preamble { get; }
    public IReadOnlyList<PatchFile> Files { get; }

    private PatchDocument(IReadOnlyList<string> preamble, IReadOnlyList<PatchFile> files)
    {
        Preamble = preamble;
        Files = files;
    }

    /// <summary>パッチらしい形（ファイルの見出しかハンクの見出し）を1つでも含むか。</summary>
    public bool HasChanges => Files.Count > 0;

    public static PatchDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var preamble = new List<string>();
        var files = new List<PatchFile>();
        var i = 0;

        while (i < lines.Length)
        {
            if (IsFileStart(lines, i))
            {
                files.Add(ParseFile(lines, ref i));
                continue;
            }
            // 見出しの無いハンクだけのパッチ（範囲を切り出して貼ったもの）も読む。
            if (HunkHeader.IsMatch(lines[i]))
            {
                files.Add(ParseFile(lines, ref i));
                continue;
            }
            if (files.Count == 0)
                preamble.Add(lines[i]);
            i++;
        }
        while (preamble.Count > 0 && preamble[^1].Length == 0)
            preamble.RemoveAt(preamble.Count - 1);
        return new PatchDocument(preamble, files);
    }

    private static bool IsFileStart(string[] lines, int i)
        => lines[i].StartsWith("diff ", StringComparison.Ordinal)
           || (lines[i].StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length
               && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal));

    private static PatchFile ParseFile(string[] lines, ref int i)
    {
        string? oldPath = null, newPath = null;
        var meta = new List<string>();
        var binary = false;

        if (lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
        {
            (oldPath, newPath) = GitHeaderPaths(lines[i]["diff --git ".Length..]);
            i++;
        }
        else if (lines[i].StartsWith("diff ", StringComparison.Ordinal))
            meta.Add(lines[i++]);

        // 拡張見出し（index・mode・rename・Binary files）と --- / +++
        while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal)
               && !lines[i].StartsWith("diff ", StringComparison.Ordinal))
        {
            var line = lines[i];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
                oldPath = MarkerPath(line[4..]);
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = MarkerPath(line[4..]);
                i++;
                break;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                oldPath = line["rename from ".Length..];
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                newPath = line["rename to ".Length..];
            else if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                meta.Add(line);
                oldPath = null;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                meta.Add(line);
                newPath = null;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                binary = true;
                meta.Add(line);
            }
            else if (line.Length > 0 && !line.StartsWith("index ", StringComparison.Ordinal))
                meta.Add(line);
            i++;
        }

        var hunks = new List<PatchHunk>();
        int added = 0, removed = 0;
        while (i < lines.Length && HunkHeader.Match(lines[i]) is { Success: true } m)
        {
            var oldLine = int.Parse(m.Groups[1].Value);
            var newLine = int.Parse(m.Groups[3].Value);
            var header = lines[i];
            i++;
            var body = new List<PatchLine>();
            while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal) && !IsFileStart(lines, i))
            {
                var line = lines[i];
                if (line.Length == 0)
                {
                    // 末尾の空行はパッチの終わり。途中の空行は文脈の空行（行頭の空白を削るエディタがある）。
                    if (i == lines.Length - 1 || RestIsBlankOrNextFile(lines, i))
                        break;
                    body.Add(new PatchLine(PatchLineKind.Context, "", oldLine++, newLine++));
                }
                else if (line[0] == '+')
                {
                    body.Add(new PatchLine(PatchLineKind.Added, line[1..], null, newLine++));
                    added++;
                }
                else if (line[0] == '-')
                {
                    body.Add(new PatchLine(PatchLineKind.Removed, line[1..], oldLine++, null));
                    removed++;
                }
                else if (line[0] == ' ')
                    body.Add(new PatchLine(PatchLineKind.Context, line[1..], oldLine++, newLine++));
                else if (line[0] == '\\')
                    body.Add(new PatchLine(PatchLineKind.NoNewline, line, null, null));
                else
                    break;   // format-patch の末尾（-- と版）など
                i++;
            }
            hunks.Add(new PatchHunk(header, body));
        }
        // ハンクの後ろに残った、次のファイルでもハンクでもない行は読み飛ばす（format-patch の署名など）。
        while (i < lines.Length && !IsFileStart(lines, i) && !HunkHeader.IsMatch(lines[i]))
            i++;

        return new PatchFile(oldPath, newPath, meta, hunks, binary) { Added = added, Removed = removed };
    }

    private static bool RestIsBlankOrNextFile(string[] lines, int i)
    {
        for (var j = i + 1; j < lines.Length; j++)
        {
            if (lines[j].Length == 0)
                continue;
            return IsFileStart(lines, j) || lines[j].StartsWith("-- ", StringComparison.Ordinal) || lines[j] == "--";
        }
        return true;
    }

    /// <summary><c>--- a/path</c> の path。<c>/dev/null</c> は null。末尾のタブ以降（diff -u の時刻）は落とす。</summary>
    private static string? MarkerPath(string raw)
    {
        var path = raw.Split('\t')[0].Trim();
        if (path == "/dev/null")
            return null;
        if (path.Length > 1 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];
        return StripPrefix(path);
    }

    private static string StripPrefix(string path)
        => path.Length > 2 && (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            ? path[2..]
            : path;

    /// <summary><c>diff --git a/x b/x</c> の2つのパス（空白入りの名前でも、同じ名前なら半分で割れる）。</summary>
    private static (string? Old, string? New) GitHeaderPaths(string rest)
    {
        if (rest.StartsWith("a/", StringComparison.Ordinal))
        {
            var half = (rest.Length - 1) / 2;
            if (rest.Length % 2 == 1 && rest[half] == ' ' && rest[(half + 3)..] == rest[2..half])
                return (rest[2..half], rest[(half + 3)..]);
            var split = rest.IndexOf(" b/", StringComparison.Ordinal);
            if (split > 0)
                return (rest[2..split], rest[(split + 3)..]);
        }
        return (null, null);
    }
}
