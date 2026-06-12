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
/// </summary>
public sealed record SideBySideRow(
    SideCellKind LeftKind, string LeftText, SideCellKind RightKind, string RightText)
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
    /// <summary><see cref="DiffUtil.Compute"/> の結果（Context/Added/Removed/Gap）を左右並びへ変換する。</summary>
    public static IReadOnlyList<SideBySideRow> Build(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<SideBySideRow>();
        var removed = new List<string>();
        var added = new List<string>();

        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Removed: removed.Add(line.Text); break;
                case DiffLineKind.Added: added.Add(line.Text); break;
                case DiffLineKind.Gap:
                    FlushChanges(rows, removed, added);
                    rows.Add(SideBySideRow.Shared(SideCellKind.Gap, line.Text));
                    break;
                default:
                    FlushChanges(rows, removed, added);
                    rows.Add(new SideBySideRow(
                        SideCellKind.Context, line.Text, SideCellKind.Context, line.Text));
                    break;
            }
        }
        FlushChanges(rows, removed, added);
        return rows;
    }

    /// <summary>
    /// git の unified diff テキストを左右並びへ変換する。ヘッダ行・ハンク見出しは左右共通行になる。
    /// </summary>
    public static IReadOnlyList<SideBySideRow> FromUnifiedPatch(string patchText)
    {
        var rows = new List<SideBySideRow>();
        var removed = new List<string>();
        var added = new List<string>();

        foreach (var raw in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            switch (ClassifyPatchLine(raw))
            {
                case SideCellKind.Removed: removed.Add(raw[1..]); break;
                case SideCellKind.Added: added.Add(raw[1..]); break;
                case SideCellKind.Gap:
                    FlushChanges(rows, removed, added);
                    rows.Add(SideBySideRow.Shared(SideCellKind.Gap, raw));
                    break;
                case SideCellKind.Header:
                    FlushChanges(rows, removed, added);
                    rows.Add(SideBySideRow.Shared(SideCellKind.Header, raw));
                    break;
                default:
                    FlushChanges(rows, removed, added);
                    var text = raw.Length > 0 ? raw[1..] : "";
                    rows.Add(new SideBySideRow(
                        SideCellKind.Context, text, SideCellKind.Context, text));
                    break;
            }
        }
        FlushChanges(rows, removed, added);
        return rows;
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
    private static void FlushChanges(List<SideBySideRow> rows, List<string> removed, List<string> added)
    {
        var count = Math.Max(removed.Count, added.Count);
        for (var i = 0; i < count; i++)
        {
            var (leftKind, leftText) = i < removed.Count
                ? (SideCellKind.Removed, removed[i]) : (SideCellKind.Empty, "");
            var (rightKind, rightText) = i < added.Count
                ? (SideCellKind.Added, added[i]) : (SideCellKind.Empty, "");
            rows.Add(new SideBySideRow(leftKind, leftText, rightKind, rightText));
        }
        removed.Clear();
        added.Clear();
    }
}
