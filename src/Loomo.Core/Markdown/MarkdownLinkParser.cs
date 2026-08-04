using System;
using System.Collections.Generic;
using System.Text;

namespace sk0ya.Loomo.Core.Markdown;

/// <summary>インラインリンク／画像 1 件。位置は解析対象文字列内のインデックス。</summary>
/// <param name="Start">開始位置（画像なら <c>!</c>、リンクなら <c>[</c>）。</param>
/// <param name="Length">閉じ <c>)</c> までを含む全体の長さ。</param>
/// <param name="Text">リンクテキスト（<c>[]</c> の中身。エスケープは解いていない生の文字列）。</param>
/// <param name="DestinationStart">宛先の開始位置（<c>&lt;&gt;</c> 形式なら <c>&lt;</c> の次）。</param>
/// <param name="DestinationLength">宛先の長さ（<c>&lt;&gt;</c> 形式なら中身だけ）。</param>
/// <param name="Destination">宛先（<c>&lt;&gt;</c> を外し、<c>\(</c> 等のエスケープを解いたもの）。</param>
/// <param name="IsImage">画像（<c>![...]</c>）か。</param>
public readonly record struct MarkdownInlineLink(
    int Start,
    int Length,
    string Text,
    int DestinationStart,
    int DestinationLength,
    string Destination,
    bool IsImage);

/// <summary>
/// Markdown のインラインリンク <c>[text](dest "title")</c> ／画像 <c>![alt](dest)</c> の解析。
///
/// <para><b>宛先は「最初の <c>)</c> まで」ではない。</b>CommonMark では <c>&lt;&gt;</c> で囲まない宛先にも
/// <b>釣り合った丸括弧</b>を含められる（<c>[aa](aa(bb)_cc.md)</c> の宛先は <c>aa(bb)_cc.md</c>）。
/// <c>[^\)]+</c> のような正規表現や <c>IndexOf(')')</c> で切ると、宛先が <c>aa(bb</c> で切れて残りが
/// 地の文として漏れる。リンクテキスト側の <c>[]</c> も同様に入れ子を許す。</para>
///
/// <para>対応する形：<c>&lt;dest&gt;</c>（空白・不釣り合いな括弧を含められる）、バックスラッシュ
/// エスケープ（<c>\(</c> <c>\)</c> <c>\&lt;</c> <c>\&gt;</c>）、省略可能なタイトル
/// （<c>"..."</c> / <c>'...'</c> / <c>(...)</c>）。</para>
/// </summary>
public static class MarkdownLinkParser
{
    /// <summary>文字列中のインラインリンク／画像を出現順に返す。参照リンク・自動リンクは対象外。</summary>
    public static IReadOnlyList<MarkdownInlineLink> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<MarkdownInlineLink>();

        var results = new List<MarkdownInlineLink>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }     // エスケープされた文字は開始記号にしない
            if (text[i] != '[') continue;
            if (!TryParseAt(text, i, out var link)) continue;

            results.Add(link);
            i = link.Start + link.Length - 1;
        }
        return results;
    }

    /// <summary>
    /// 画像 <c>![alt](src)</c> だけを、<b>リンクテキストの内側も含めて</b>出現順に返す。
    /// バッジ記法 <c>[![alt](badge.svg)](url)</c> のように画像がリンクの中に入る形があるため、
    /// 「画像を先に処理してからリンクを処理する」段取りではこちらを使う。
    /// </summary>
    public static IReadOnlyList<MarkdownInlineLink> FindImages(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<MarkdownInlineLink>();

        var results = new List<MarkdownInlineLink>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] != '!' || i + 1 >= text.Length || text[i + 1] != '[') continue;
            if (!TryParseAt(text, i + 1, out var link) || !link.IsImage) continue;

            results.Add(link);
            i = link.Start + link.Length - 1;
        }
        return results;
    }

    /// <summary>
    /// <paramref name="bracket"/>（<c>[</c> の位置）から始まるインラインリンク／画像を解析する。
    /// 直前が <c>!</c> なら画像として扱い、<see cref="MarkdownInlineLink.Start"/> はその <c>!</c> を指す。
    /// </summary>
    public static bool TryParseAt(string text, int bracket, out MarkdownInlineLink link)
    {
        link = default;
        if (string.IsNullOrEmpty(text) || bracket < 0 || bracket >= text.Length || text[bracket] != '[')
            return false;

        if (!TryScanBalanced(text, bracket, '[', ']', out int textEnd))
            return false;

        int paren = textEnd + 1;                        // ']' の次
        if (paren >= text.Length || text[paren] != '(')
            return false;

        if (!TryParseDestination(text, paren, out int destStart, out int destLength, out bool angled, out int close))
            return false;

        bool isImage = bracket > 0 && text[bracket - 1] == '!'
            && !(bracket > 1 && text[bracket - 2] == '\\');
        int start = isImage ? bracket - 1 : bracket;

        link = new MarkdownInlineLink(
            start,
            close + 1 - start,
            text[(bracket + 1)..textEnd],
            destStart,
            destLength,
            Unescape(text.Substring(destStart, destLength), angled),
            isImage);
        return true;
    }

    /// <summary>
    /// <paramref name="open"/>（<c>(</c> の位置）から宛先と省略可能なタイトルを読み、閉じ <c>)</c> の位置を返す。
    /// 宛先の丸括弧は釣り合っていれば宛先の一部として扱う。
    /// </summary>
    /// <param name="angled">宛先が <c>&lt;...&gt;</c> 形式だったか（<paramref name="destStart"/> は中身を指す）。</param>
    public static bool TryParseDestination(
        string text, int open, out int destStart, out int destLength, out bool angled, out int close)
    {
        destStart = 0;
        destLength = 0;
        angled = false;
        close = -1;
        if (open < 0 || open >= text.Length || text[open] != '(') return false;

        int i = open + 1;
        i = SkipWhitespace(text, i);
        if (i >= text.Length) return false;

        if (text[i] == '<')
        {
            // <dest>：空白も不釣り合いな括弧も入れられる。改行と未エスケープの '<' は不可。
            destStart = i + 1;
            int j = destStart;
            while (j < text.Length && text[j] != '>' && text[j] != '\n' && text[j] != '<')
            {
                if (text[j] == '\\' && j + 1 < text.Length) j++;
                j++;
            }
            if (j >= text.Length || text[j] != '>') return false;
            destLength = j - destStart;
            angled = true;
            i = j + 1;
        }
        else
        {
            destStart = i;
            int depth = 0;
            int j = i;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == '\\' && j + 1 < text.Length) { j += 2; continue; }
                if (char.IsWhiteSpace(c)) break;        // ここから先はタイトル
                if (c == '(') depth++;
                else if (c == ')')
                {
                    if (depth == 0) break;              // このリンクを閉じる ')'
                    depth--;
                }
                j++;
            }
            if (depth != 0) return false;               // 釣り合っていない括弧は宛先として不正
            destLength = j - destStart;
            i = j;
        }

        i = SkipWhitespace(text, i);
        if (i < text.Length && text[i] is '"' or '\'' or '(')
        {
            char open2 = text[i];
            char close2 = open2 == '(' ? ')' : open2;
            int j = i + 1;
            while (j < text.Length && text[j] != close2)
            {
                if (text[j] == '\\' && j + 1 < text.Length) j++;
                j++;
            }
            if (j >= text.Length) return false;
            i = SkipWhitespace(text, j + 1);
        }

        if (i >= text.Length || text[i] != ')') return false;
        close = i;
        return true;
    }

    /// <summary>宛先のエスケープ（<c>\(</c> 等）を解く。<c>&lt;&gt;</c> 形式では <c>&lt;</c>/<c>&gt;</c> も対象。</summary>
    public static string Unescape(string destination, bool angled = false)
    {
        if (destination.IndexOf('\\') < 0) return destination;

        var sb = new StringBuilder(destination.Length);
        for (int i = 0; i < destination.Length; i++)
        {
            if (destination[i] == '\\' && i + 1 < destination.Length && IsEscapable(destination[i + 1], angled))
            {
                sb.Append(destination[i + 1]);
                i++;
                continue;
            }
            sb.Append(destination[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Markdown の宛先として安全に書けるよう整形する。釣り合わない括弧・空白・
    /// <c>&lt;</c><c>&gt;</c> を含むなら <c>&lt;...&gt;</c> で囲み、必要な文字だけをエスケープする。
    /// リンクを<b>生成</b>する側（HTML→Markdown 変換・画像貼り付け）はこれを通す。
    /// </summary>
    public static string EncodeDestination(string? destination)
    {
        var dest = destination ?? "";
        if (dest.Length == 0) return dest;

        bool needsAngle = false;
        int depth = 0;
        foreach (var c in dest)
        {
            if (char.IsWhiteSpace(c) || c is '<' or '>') { needsAngle = true; continue; }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) { needsAngle = true; } else depth--; }
        }
        if (depth != 0) needsAngle = true;

        if (!needsAngle) return dest.Replace("\\", "\\\\");

        var sb = new StringBuilder(dest.Length + 2).Append('<');
        foreach (var c in dest)
        {
            if (c is '\\' or '<' or '>') sb.Append('\\');
            sb.Append(c == '\n' ? ' ' : c);
        }
        return sb.Append('>').ToString();
    }

    /// <summary><paramref name="open"/> から釣り合った閉じ記号を探す（エスケープを尊重）。</summary>
    private static bool TryScanBalanced(string text, int open, char openChar, char closeChar, out int close)
    {
        close = -1;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '\n' && i + 1 < text.Length && text[i + 1] == '\n') return false; // 空行はインライン要素を跨がない
            if (c == openChar) depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0) { close = i; return true; }
            }
        }
        return false;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static bool IsEscapable(char c, bool angled) =>
        c is '(' or ')' or '\\' || (angled && c is '<' or '>');
}
