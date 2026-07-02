using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>左右並び差分の1セルの種別。Empty は片側だけ行がある箇所の詰め物。</summary>
public enum SideCellKind
{
    Empty,
    Context,
    Added,
    Removed,
    Gap,     // 省略マーカー／ハンク見出し（左右共通の1行として表示する）
    Header   // git パッチのヘッダ行（左右共通の1行として表示する）
}

/// <summary>
/// 左右並び差分の1行。Gap / Header は左右共通行（両セルに同じ種別・同じテキストが入る）。
/// <paramref name="LeftLine"/> / <paramref name="RightLine"/> は1始まりの行番号（その側に行が無い
/// Empty セルや共通行では null）。実際のファイルのように行番号付きで対比するために使う。
/// </summary>
public sealed record SideBySideRow(
    SideCellKind LeftKind, string LeftText, SideCellKind RightKind, string RightText,
    int? LeftLine = null, int? RightLine = null)
{
    /// <summary>左右共通で表示する1行（ヘッダ・省略マーカー）を作る。</summary>
    public static SideBySideRow Shared(SideCellKind kind, string text) => new(kind, text, kind, text);
}

/// <summary>
/// 統合（unified）形式の差分を左右並び（side-by-side）の行列へ変換する。UI 非依存。
/// 削除行は左・追加行は右に置き、連続する削除／追加のかたまりを行単位で対にする。
/// </summary>
public static class SideBySideDiff
{
    /// <summary>
    /// <see cref="DiffUtil.Compute"/> / <see cref="DiffUtil.ComputeFull"/> の結果
    /// （Context/Added/Removed/Gap）を左右並びへ変換する。各行に1始まりの行番号を付ける。
    /// </summary>
    public static IReadOnlyList<SideBySideRow> Build(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<SideBySideRow>();
        var removed = new List<(string Text, int Line)>();
        var added = new List<(string Text, int Line)>();
        var oldLine = 1;
        var newLine = 1;

        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Removed: removed.Add((line.Text, oldLine++)); break;
                case DiffLineKind.Added: added.Add((line.Text, newLine++)); break;
                case DiffLineKind.Gap:
                    FlushChanges(rows, removed, added);
                    rows.Add(SideBySideRow.Shared(SideCellKind.Gap, line.Text));
                    break;
                default:
                    FlushChanges(rows, removed, added);
                    rows.Add(new SideBySideRow(
                        SideCellKind.Context, line.Text, SideCellKind.Context, line.Text,
                        oldLine++, newLine++));
                    break;
            }
        }
        FlushChanges(rows, removed, added);
        return rows;
    }

    /// <summary>
    /// git の unified diff テキストを左右並びへ変換する。行番号はハンク見出し（<c>@@</c>）から復元する。
    /// <paramref name="hideChrome"/> が true のときは git ヘッダ行・ハンク見出しを行に含めない
    /// （全文コンテキストの差分を「実際のファイルのように」見せる左右表示用）。
    /// </summary>
    public static IReadOnlyList<SideBySideRow> FromUnifiedPatch(string patchText, bool hideChrome = false)
    {
        var rows = new List<SideBySideRow>();
        var removed = new List<(string Text, int Line)>();
        var added = new List<(string Text, int Line)>();
        var oldLine = 0;
        var newLine = 0;

        foreach (var raw in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            switch (ClassifyPatchLine(raw))
            {
                case SideCellKind.Removed: removed.Add((raw[1..], oldLine++)); break;
                case SideCellKind.Added: added.Add((raw[1..], newLine++)); break;
                case SideCellKind.Gap:
                    FlushChanges(rows, removed, added);
                    if (TryParseHunkStarts(raw, out var o, out var n)) { oldLine = o; newLine = n; }
                    if (!hideChrome) rows.Add(SideBySideRow.Shared(SideCellKind.Gap, raw));
                    break;
                case SideCellKind.Header:
                    FlushChanges(rows, removed, added);
                    if (!hideChrome) rows.Add(SideBySideRow.Shared(SideCellKind.Header, raw));
                    break;
                default:
                    FlushChanges(rows, removed, added);
                    var text = raw.Length > 0 ? raw[1..] : "";
                    rows.Add(new SideBySideRow(
                        SideCellKind.Context, text, SideCellKind.Context, text, oldLine++, newLine++));
                    break;
            }
        }
        FlushChanges(rows, removed, added);
        return rows;
    }

    /// <summary>ハンク見出し <c>@@ -a,b +c,d @@</c> から左右の開始行番号（a, c）を取り出す。</summary>
    public static bool TryParseHunkStarts(string line, out int oldStart, out int newStart)
    {
        oldStart = newStart = 0;
        var m = System.Text.RegularExpressions.Regex.Match(
            line, @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
        if (!m.Success) return false;
        oldStart = int.Parse(m.Groups[1].Value);
        newStart = int.Parse(m.Groups[2].Value);
        return true;
    }

    /// <summary>git の unified diff 1行を表示種別へ分類する（Empty は返さない）。</summary>
    public static SideCellKind ClassifyPatchLine(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("diff ", StringComparison.Ordinal)
            || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("new file", StringComparison.Ordinal)
            || line.StartsWith("deleted file", StringComparison.Ordinal)
            || line.StartsWith("rename ", StringComparison.Ordinal)
            || line.StartsWith("similarity ", StringComparison.Ordinal)
            || line.StartsWith("Binary files", StringComparison.Ordinal)
            || line.StartsWith("\\", StringComparison.Ordinal)
            || line.StartsWith("#", StringComparison.Ordinal))
            return SideCellKind.Header;
        if (line.StartsWith("@@", StringComparison.Ordinal)) return SideCellKind.Gap;
        if (line.StartsWith("+", StringComparison.Ordinal)) return SideCellKind.Added;
        if (line.StartsWith("-", StringComparison.Ordinal)) return SideCellKind.Removed;
        return SideCellKind.Context;
    }

    /// <summary>溜めた削除（左）と追加（右）を行単位で対にして吐き出す。足りない側は Empty で埋める。</summary>
    private static void FlushChanges(
        List<SideBySideRow> rows,
        List<(string Text, int Line)> removed,
        List<(string Text, int Line)> added)
    {
        var count = Math.Max(removed.Count, added.Count);
        for (var i = 0; i < count; i++)
        {
            var (leftKind, leftText, leftLine) = i < removed.Count
                ? (SideCellKind.Removed, removed[i].Text, (int?)removed[i].Line)
                : (SideCellKind.Empty, "", (int?)null);
            var (rightKind, rightText, rightLine) = i < added.Count
                ? (SideCellKind.Added, added[i].Text, (int?)added[i].Line)
                : (SideCellKind.Empty, "", (int?)null);
            rows.Add(new SideBySideRow(leftKind, leftText, rightKind, rightText, leftLine, rightLine));
        }
        removed.Clear();
        added.Clear();
    }
}
