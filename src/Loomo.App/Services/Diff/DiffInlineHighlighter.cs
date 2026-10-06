using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 差分本体の行内差分（書き換えた行の中で、実際に変わった文字の範囲）を表示行へ配る。UI 非依存。
///
/// <para>行の背景色だけでは「1文字直した行」と「丸ごと書き換えた行」が同じに見える。書き換えのかたまり
/// （削除行の並び＋追加行の並び）ごとに、似ている行どうしを対にして（<see cref="LinePairing"/>）
/// その2行の行内差分（<see cref="InlineDiff"/>）を取る。似ていない行・対にならなかった行には付けない。</para>
/// </summary>
internal static class DiffInlineHighlighter
{
    /// <summary>これを超える行数の差分は行内差分を取らない（構文色と同じ頭打ち）。</summary>
    internal const int MaxLines = DiffSyntaxHighlighter.MaxLines;

    /// <summary>行内差分を付けない差分（どの行にも範囲が無い）。</summary>
    internal static IReadOnlyList<IReadOnlyList<TextSpan>?> None { get; } = Array.Empty<IReadOnlyList<TextSpan>?>();

    /// <summary>
    /// 統合表示の行と1対1の範囲（範囲の無い行は null）。<paramref name="hasPatchPrefix"/> のときは各行の
    /// 先頭1文字（<c>+</c>／<c>-</c>）を剥がして比べ、返す範囲は1桁ずらす（表示行の桁で返す）。
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<TextSpan>?> ForUnified(IReadOnlyList<DiffRowVm> rows, bool hasPatchPrefix)
    {
        if (rows.Count == 0 || rows.Count > MaxLines) return None;
        var result = new IReadOnlyList<TextSpan>?[rows.Count];
        var offset = hasPatchPrefix ? 1 : 0;
        var any = false;
        var i = 0;
        while (i < rows.Count)
        {
            if (rows[i].Kind is not ("Removed" or "Added"))
            {
                i++;
                continue;
            }
            // 書き換えのかたまり：削除行の並び（→ 追加行の並び）。統合表示では削除が先に並ぶ。
            var removed = new List<int>();
            var added = new List<int>();
            while (i < rows.Count && rows[i].Kind == "Removed") removed.Add(i++);
            while (i < rows.Count && rows[i].Kind == "Added") added.Add(i++);
            if (removed.Count == 0 || added.Count == 0) continue;

            var pairs = LinePairing.Pair(
                removed.ConvertAll(r => Body(rows[r].Text, offset)),
                added.ConvertAll(a => Body(rows[a].Text, offset)));
            foreach (var (r, a) in pairs)
            {
                if (r < 0 || a < 0) continue;
                var (leftRow, rightRow) = (removed[r], added[a]);
                if (InlineDiff.Compute(Body(rows[leftRow].Text, offset), Body(rows[rightRow].Text, offset)) is not { } change)
                    continue;
                result[leftRow] = Shift(change.Left, offset);
                result[rightRow] = Shift(change.Right, offset);
                any = true;
            }
        }
        return any ? result : None;
    }

    /// <summary>
    /// 左右並びの1行（同じ高さに並んだ削除行と追加行）の行内差分。片側だけの行・似ていない2行は null。
    /// </summary>
    internal static InlineChange? ForSideRow(DiffSideRowVm row)
        => row is { LeftKind: "Removed", RightKind: "Added" }
            ? InlineDiff.Compute(row.LeftText, row.RightText)
            : null;

    private static string Body(string text, int offset) => offset > 0 && text.Length >= offset ? text[offset..] : text;

    private static IReadOnlyList<TextSpan> Shift(IReadOnlyList<TextSpan> spans, int offset)
    {
        if (offset == 0) return spans;
        var shifted = new TextSpan[spans.Count];
        for (var k = 0; k < spans.Count; k++)
            shifted[k] = spans[k] with { Start = spans[k].Start + offset };
        return shifted;
    }
}

/// <summary>
/// 左右並びの行内差分の覚え書き。左右の本文の組 → 行内差分。右を編集するたびに装飾を組み直すが、
/// 変わるのは打っている1行だけなので、それ以外は前回の結果を使い回す（UI スレッドの組み立てを軽く保つ）。
///
/// <para>組み直しのたびに <see cref="Rotate"/> を呼ぶ。その回で使われなかった組は次の回で捨てる
/// （ファイルを替えても前のファイルの行が溜まり続けない）。</para>
/// </summary>
internal sealed class DiffInlineCache
{
    private Dictionary<(string Left, string Right), InlineChange?> _current = new();
    private Dictionary<(string Left, string Right), InlineChange?> _previous = new();

    /// <summary>1回分の組み立ての始まり。前の回に使った組だけを残す。</summary>
    internal void Rotate()
    {
        _previous = _current;
        _current = new Dictionary<(string Left, string Right), InlineChange?>(_previous.Count);
    }

    internal InlineChange? Get(DiffSideRowVm row)
    {
        var key = (row.LeftText, row.RightText);
        if (_current.TryGetValue(key, out var change)) return change;
        if (!_previous.TryGetValue(key, out change))
            change = DiffInlineHighlighter.ForSideRow(row);
        _current[key] = change;
        return change;
    }

    /// <summary>覚えている組の数（テスト用）。</summary>
    internal int Count => _current.Count;
}
