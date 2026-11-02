using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>Editor ペインのアウトラインの 1 行（見出し／宣言）。行・列は 0 始まり。</summary>
public sealed record EditorOutlineEntry(string Text, int Depth, int Line0, int Column0)
{
    /// <summary>深さぶんの字下げ（一覧の行の Margin にそのまま使う）。</summary>
    public Thickness Indent => new(Depth * 12, 0, 0, 0);
}

/// <summary>
/// Editor ペインのアウトラインの中身を組む純ロジック。Markdown は本文の見出しから、それ以外は言語サーバーの
/// documentSymbol を宣言順に平らにして作る。
/// </summary>
internal static class EditorOutline
{
    /// <summary>この拡張子を Markdown として見出しから組むか。</summary>
    public static bool IsMarkdown(string? filePath)
        => !string.IsNullOrEmpty(filePath)
           && System.IO.Path.GetExtension(filePath).ToLowerInvariant() is ".md" or ".markdown" or ".mdx" or ".mkd";

    /// <summary>言語サーバーのシンボルの木を、宣言順・深さ付きの一覧にする（飛び先は名前の位置）。</summary>
    public static IReadOnlyList<EditorOutlineEntry> FromSymbols(IReadOnlyList<DocumentSymbol>? symbols)
    {
        var result = new List<EditorOutlineEntry>();
        void Walk(IReadOnlyList<DocumentSymbol>? nodes, int depth)
        {
            if (nodes is null)
                return;
            foreach (var s in nodes)
            {
                var start = s.SelectionRange?.Start ?? s.Range?.Start;
                result.Add(new EditorOutlineEntry(s.Name ?? "", depth, start?.Line ?? 0, start?.Character ?? 0));
                Walk(s.Children, depth + 1);
            }
        }
        Walk(symbols, 0);
        return result;
    }

    /// <summary>
    /// Markdown の見出し（<c># 見出し</c> と、次の行が <c>===</c>／<c>---</c> の形）を一覧にする。深さは文書で一番浅い
    /// 見出しを 0 とした差。フェンス付きコードブロックの中と先頭のフロントマターは見出しにしない。
    /// </summary>
    public static IReadOnlyList<EditorOutlineEntry> FromMarkdown(IReadOnlyList<string> lines)
    {
        var found = new List<(int Level, string Text, int Line0, int Col0)>();
        var start = SkipFrontMatter(lines);
        string? fence = null;
        var paragraph = false;   // 直前の行が段落の本文か（下線形の見出しの対象になれるか）

        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];

            if (Fence.Match(line) is { Success: true } f)
            {
                var marker = f.Groups[1].Value;
                if (fence is null)
                    fence = marker;
                else if (marker[0] == fence[0] && marker.Length >= fence.Length && line.Trim() == marker)
                    fence = null;
                paragraph = false;
                continue;
            }
            if (fence is not null)
                continue;

            if (Atx.Match(line) is { Success: true } atx)
            {
                var text = Clean(AtxClosing.Replace(atx.Groups[3].Value, ""));
                if (text.Trim('#').Length > 0)
                    found.Add((atx.Groups[2].Length, text, i, atx.Groups[3].Index));
                paragraph = false;
                continue;
            }

            if (paragraph && SetextUnderline.Match(line) is { Success: true } setext)
            {
                var prev = lines[i - 1];
                var text = Clean(prev);
                if (text.Length > 0)
                    found.Add((setext.Groups[1].Value[0] == '=' ? 1 : 2, text, i - 1, prev.Length - prev.TrimStart().Length));
                paragraph = false;
                continue;
            }

            // 段落の本文になれる行か（「- 項目」や区切り線の次の --- は見出しの下線ではない）。
            var trimmed = line.TrimStart();
            paragraph = trimmed.Length > 0
                        && line.Length - trimmed.Length < 4
                        && !trimmed.StartsWith('>')
                        && !trimmed.StartsWith('|')
                        && !SetextUnderline.IsMatch(line)
                        && !IsListMarker(trimmed);
        }

        if (found.Count == 0)
            return Array.Empty<EditorOutlineEntry>();
        var min = 6;
        foreach (var h in found)
            min = Math.Min(min, h.Level);
        var result = new List<EditorOutlineEntry>(found.Count);
        foreach (var h in found)
            result.Add(new EditorOutlineEntry(h.Text, h.Level - min, h.Line0, h.Col0));
        return result;
    }

    private static readonly Regex Atx = new(@"^( {0,3})(#{1,6})(?:[ \t]+(.*?))?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex AtxClosing = new(@"[ \t]+#+$", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex SetextUnderline = new(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__|`)", RegexOptions.Compiled);

    private static bool IsListMarker(string trimmed)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] is ' ' or '\t')
            return true;
        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
            digits++;
        return digits > 0 && digits + 1 < trimmed.Length
               && trimmed[digits] is '.' or ')' && trimmed[digits + 1] is ' ' or '\t';
    }

    private static int SkipFrontMatter(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
            return 0;
        for (var i = 1; i < lines.Count; i++)
            if (lines[i].TrimEnd() is "---" or "...")
                return i + 1;
        return 0;
    }

    /// <summary>リンクは文字だけ、強調と `code` の印は落とす。</summary>
    private static string Clean(string text)
        => Emphasis.Replace(Link.Replace(text, "$1"), "").Trim();
}
