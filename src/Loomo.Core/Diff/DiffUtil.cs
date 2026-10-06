using System;
using System.Collections.Generic;
using System.Text;

namespace sk0ya.Loomo.Core.Diff;

public enum DiffLineKind
{
    Context,  // 変更なし（前後の文脈）
    Added,    // 追加行
    Removed,  // 削除行
    Gap       // 省略マーカー（… N行省略）
}

/// <summary>
/// 差分の1行。<paramref name="OldText"/> は空白を無視した比較で「同じ」とみなした文脈行のうち、旧側の綴りが
/// 新側と違うものだけに入る（それ以外は null ＝旧側も <paramref name="Text"/> と同じ）。
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, string? OldText = null)
{
    /// <summary>旧側（左）の綴り。</summary>
    public string LeftText => OldText ?? Text;
}

/// <summary>行差分の比べ方。</summary>
/// <param name="IgnoreWhitespace">空白の違いを無視する（<c>git diff -w</c> と同じ：行の中の空白をすべて除いて比べる）。</param>
public sealed record DiffOptions(bool IgnoreWhitespace = false)
{
    public static DiffOptions Default { get; } = new();
}

/// <summary>
/// 行単位の差分（<see cref="MyersDiff"/>）を計算し、変更箇所の周辺だけを抜き出した「ハンク」形式で返す。
/// ファイル編集ツールの承認カードで色付き差分を見せるために使う。UI 非依存。
/// </summary>
public static class DiffUtil
{
    /// <summary>追加/削除の行数を数える。</summary>
    public static (int added, int removed) Stat(string oldText, string newText, DiffOptions? options = null)
    {
        var added = 0;
        var removed = 0;
        foreach (var op in RawDiff(Split(oldText), Split(newText), options ?? DiffOptions.Default))
        {
            if (op.Kind == DiffLineKind.Added) added++;
            else if (op.Kind == DiffLineKind.Removed) removed++;
        }
        return (added, removed);
    }

    /// <summary>変更箇所の周辺 <paramref name="context"/> 行だけを残したハンクを返す。</summary>
    public static IReadOnlyList<DiffLine> Compute(
        string oldText, string newText, int context = 3, DiffOptions? options = null)
        => Hunkify(RawDiff(Split(oldText), Split(newText), options ?? DiffOptions.Default), context);

    /// <summary>
    /// 全行を Context/Added/Removed で返す（ハンク化・Gap 省略なし）。左右並びで実際のファイルのように
    /// 全文を対比するために使う。
    /// </summary>
    public static IReadOnlyList<DiffLine> ComputeFull(string oldText, string newText, DiffOptions? options = null)
        => RawDiff(Split(oldText), Split(newText), options ?? DiffOptions.Default);

    /// <summary>差分行を +/-/空白/… 接頭辞付きのテキストへ整形する（承認サマリ用）。</summary>
    public static string ToUnifiedText(IReadOnlyList<DiffLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            switch (l.Kind)
            {
                case DiffLineKind.Added: sb.Append('+').Append(l.Text); break;
                case DiffLineKind.Removed: sb.Append('-').Append(l.Text); break;
                case DiffLineKind.Gap: sb.Append('⋯').Append(l.Text); break;
                default: sb.Append(' ').Append(l.Text); break;
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// git の unified diff テキストを <see cref="DiffLine"/> の並びへ戻す（ヘッダ行と <c>@@</c> は落とす）。
    /// <b>全文コンテキストのパッチ専用</b>——ハンク化されたパッチを渡すと、畳まれた文脈行が黙って
    /// 抜けた「別の文書」になる。<see cref="ComputeFull"/> と同じ形（Gap 無し）を、git が既に計算済みの
    /// 差分から得るための入口。
    ///
    /// <para><b>本文はハンクの中だけ</b>から拾う。ハンクの外の行（<c>diff --git</c> 等のヘッダ、git が
    /// 返したエラーメッセージ）は本文ではないので落とす——先頭1文字を剥がして文脈行にすると、
    /// <c>fatal: …</c> が <c>atal: …</c> という本文の1行として文書に紛れ込む。そしてハンクの中では
    /// 先頭1文字だけで分類する（<see cref="SideBySideDiff.ClassifyPatchLine(string, bool)"/>）ので、
    /// <c>---</c>（水平線・フロントマターの囲み・setext 見出しの下線）の削除行や <c>+++</c> の追加行も
    /// 落ちない。ハンクが1つも無ければ空を返すので、呼び元は「差分が無い」と「解釈できなかった」を
    /// パッチ本文が空かどうかで見分けられる。</para>
    /// </summary>
    public static IReadOnlyList<DiffLine> FromUnifiedPatch(string patchText)
    {
        var lines = new List<DiffLine>();
        var inHunk = false;
        foreach (var raw in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            var kind = SideBySideDiff.ClassifyPatchLine(raw, inHunk);
            if (kind == SideCellKind.Gap)
            {
                inHunk = true;      // ハンク見出しそのものは本文ではない
                continue;
            }
            if (kind == SideCellKind.Header)
            {
                // 「\ No newline at end of file」はハンクの中のマーカー（ハンクはまだ続く）。
                if (!raw.StartsWith("\\", StringComparison.Ordinal)) inHunk = false;
                continue;
            }
            if (!inHunk)
                continue;           // ハンクの外の行は本文ではない
            lines.Add(new DiffLine(
                kind switch
                {
                    SideCellKind.Added => DiffLineKind.Added,
                    SideCellKind.Removed => DiffLineKind.Removed,
                    _ => DiffLineKind.Context,
                },
                raw.Length > 0 ? raw[1..] : ""));
        }
        return lines;
    }

    private static string[] Split(string text)
        => text.Length == 0 ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');

    // ===== 全行を Context/Added/Removed に分類した生の差分 =====
    //
    // 行を整数へ写して（同じ行＝同じ番号）Myers 法で差分を取り、変更のかたまりを「削除 → 追加」の順へ
    // 揃えてから、片側だけのかたまりを読みやすい位置へ寄せる（DiffCompaction）。
    private static List<DiffLine> RawDiff(string[] a, string[] b, DiffOptions options)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var aKeys = ToKeys(a, ids, options);
        var bKeys = ToKeys(b, ids, options);
        var edits = OrderChangeRuns(MyersDiff.Diff(aKeys, bKeys));
        DiffCompaction.Compact(edits, aKeys, bKeys, Blanks(a), Blanks(b));

        var result = new List<DiffLine>(edits.Count);
        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case EditKind.Delete:
                    result.Add(new DiffLine(DiffLineKind.Removed, a[edit.A]));
                    break;
                case EditKind.Insert:
                    result.Add(new DiffLine(DiffLineKind.Added, b[edit.B]));
                    break;
                default:
                    var (oldLine, newLine) = (a[edit.A], b[edit.B]);
                    result.Add(new DiffLine(
                        DiffLineKind.Context, newLine, oldLine == newLine ? null : oldLine));
                    break;
            }
        }
        return result;
    }

    private static int[] ToKeys(string[] lines, Dictionary<string, int> ids, DiffOptions options)
    {
        var keys = new int[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var key = options.IgnoreWhitespace ? WithoutWhitespace(lines[i]) : lines[i];
            if (!ids.TryGetValue(key, out var id))
                ids[key] = id = ids.Count;
            keys[i] = id;
        }
        return keys;
    }

    private static string WithoutWhitespace(string line)
    {
        var any = false;
        foreach (var ch in line)
            if (char.IsWhiteSpace(ch)) { any = true; break; }
        if (!any) return line;
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static bool[] Blanks(string[] lines)
    {
        var blank = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            blank[i] = string.IsNullOrWhiteSpace(lines[i]);
        return blank;
    }

    /// <summary>一致行に挟まれた変更のかたまりの中を「削除 → 追加」の順へ並べ替える（それぞれの順序は保つ）。</summary>
    private static List<Edit> OrderChangeRuns(List<Edit> edits)
    {
        var ordered = new List<Edit>(edits.Count);
        var inserts = new List<Edit>();
        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case EditKind.Equal:
                    ordered.AddRange(inserts);
                    inserts.Clear();
                    ordered.Add(edit);
                    break;
                case EditKind.Delete:
                    ordered.Add(edit);
                    break;
                default:
                    inserts.Add(edit);
                    break;
            }
        }
        ordered.AddRange(inserts);
        return ordered;
    }

    // ===== 変更周辺だけ残し、長い無変更区間を Gap に畳む =====
    private static List<DiffLine> Hunkify(List<DiffLine> raw, int context)
    {
        // 各行を残すか（変更行＝常に残す / コンテキスト行＝変更の context 行以内なら残す）
        var keep = new bool[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i].Kind is DiffLineKind.Added or DiffLineKind.Removed)
            {
                var lo = Math.Max(0, i - context);
                var hi = Math.Min(raw.Count - 1, i + context);
                for (var k = lo; k <= hi; k++) keep[k] = true;
            }
        }

        var result = new List<DiffLine>();
        var idx = 0;
        while (idx < raw.Count)
        {
            if (keep[idx])
            {
                result.Add(raw[idx]);
                idx++;
            }
            else
            {
                var start = idx;
                while (idx < raw.Count && !keep[idx]) idx++;
                var skipped = idx - start;
                if (skipped > 0)
                    result.Add(new DiffLine(DiffLineKind.Gap, $" … {skipped} 行省略 …"));
            }
        }
        return result;
    }
}
