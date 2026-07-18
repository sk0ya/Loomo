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

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>
/// 行単位の差分（LCS）を計算し、変更箇所の周辺だけを抜き出した「ハンク」形式で返す。
/// ファイル編集ツールの承認カードで色付き差分を見せるために使う。UI 非依存。
/// </summary>
public static class DiffUtil
{
    private const int MaxDpLines = 5000; // これを超える巨大ファイルは全置換表示にフォールバック

    /// <summary>追加/削除の行数を数える。</summary>
    public static (int added, int removed) Stat(string oldText, string newText)
    {
        var added = 0;
        var removed = 0;
        foreach (var op in RawDiff(Split(oldText), Split(newText)))
        {
            if (op.Kind == DiffLineKind.Added) added++;
            else if (op.Kind == DiffLineKind.Removed) removed++;
        }
        return (added, removed);
    }

    /// <summary>変更箇所の周辺 <paramref name="context"/> 行だけを残したハンクを返す。</summary>
    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText, int context = 3)
        => Hunkify(RawDiff(Split(oldText), Split(newText)), context);

    /// <summary>
    /// 全行を Context/Added/Removed で返す（ハンク化・Gap 省略なし）。左右並びで実際のファイルのように
    /// 全文を対比するために使う。
    /// </summary>
    public static IReadOnlyList<DiffLine> ComputeFull(string oldText, string newText)
        => RawDiff(Split(oldText), Split(newText));

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

    private static string[] Split(string text)
        => text.Length == 0 ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');

    // ===== 全行を Context/Added/Removed に分類した生の差分 =====
    private static List<DiffLine> RawDiff(string[] a, string[] b)
    {
        var result = new List<DiffLine>();

        // 巨大ファイルは DP を避け、全削除→全追加で表現する
        if ((long)a.Length * b.Length > (long)MaxDpLines * MaxDpLines)
        {
            foreach (var line in a) result.Add(new DiffLine(DiffLineKind.Removed, line));
            foreach (var line in b) result.Add(new DiffLine(DiffLineKind.Added, line));
            return result;
        }

        // LCS 長さの DP テーブル
        var n = a.Length;
        var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // バックトラックして差分列を復元
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffLineKind.Context, a[x]));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, a[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, b[y]));
                y++;
            }
        }
        while (x < n) result.Add(new DiffLine(DiffLineKind.Removed, a[x++]));
        while (y < m) result.Add(new DiffLine(DiffLineKind.Added, b[y++]));
        return result;
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
