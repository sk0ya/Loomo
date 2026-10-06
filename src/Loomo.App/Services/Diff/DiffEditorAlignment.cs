using Editor.Core.Editing;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>左右並び差分の片側：エディタに載せる本文の行と、行の背景・空き行の装飾。</summary>
public sealed record DiffEditorSide(IReadOnlyList<string> Lines, DiffDecorations Decorations)
{
    public string Text => string.Join("\n", Lines);
}

/// <summary>
/// 左右並び差分の行（<see cref="DiffSideRowVm"/>）を、左右2つのエディタへ載せる形へ組み替える。UI 非依存。
///
/// <para>行 i は左右どちらのエディタでも<b>表示行 i</b> に来る——片側にしか無い行のぶんは反対側へ
/// 空き行（<see cref="DiffDecorations.SpacersBefore"/>）を挿すので、行番号・スクロール位置・中央の帯の
/// 位置はすべて「行の添字 × 行の高さ」で左右共通に決まる。</para>
///
/// <para>エディタのバッファは末尾改行を「空の最終行」として持つ（<c>"a\nb\n"</c> は3行）が、git の行には
/// それが無い。空の最終行・空文書の1行のように<b>行に対応しないエディタの行</b>が末尾に出るぶんは、
/// 反対側の末尾にも空き行を足して高さを揃える（<see cref="Build(IReadOnlyList{DiffSideRowVm}, int, int)"/>）。</para>
/// </summary>
public static class DiffEditorAlignment
{
    /// <summary>行から左右それぞれの本文の行を取り出す（その側に行番号がある行だけ）。</summary>
    public static (List<string> Left, List<string> Right) SideLines(IReadOnlyList<DiffSideRowVm> rows)
    {
        var left = new List<string>();
        var right = new List<string>();
        foreach (var row in rows)
        {
            if (HasLine(row.LeftLine)) left.Add(left.Count == 0 ? WithoutBom(row.LeftText) : row.LeftText);
            if (HasLine(row.RightLine)) right.Add(right.Count == 0 ? WithoutBom(row.RightText) : row.RightText);
        }
        return (left, right);
    }

    /// <summary>
    /// 左右の装飾を組み立てる。<paramref name="leftEditorLineCount"/> / <paramref name="rightEditorLineCount"/> は
    /// 実際にエディタが持っている行数で、行から取り出した本文の行数より多いぶん（空の最終行）を反対側の
    /// 末尾の空き行で埋める。
    /// </summary>
    public static (DiffEditorSide Left, DiffEditorSide Right) Build(
        IReadOnlyList<DiffSideRowVm> rows, int leftEditorLineCount, int rightEditorLineCount,
        Func<DiffSideRowVm, InlineChange?>? inlineOf = null)
    {
        var inline = InlineChanges(rows, inlineOf ?? DiffInlineHighlighter.ForSideRow);
        var left = BuildSide(rows, inline, left: true);
        var right = BuildSide(rows, inline, left: false);
        var leftExtra = Math.Max(0, leftEditorLineCount - left.Lines.Count);
        var rightExtra = Math.Max(0, rightEditorLineCount - right.Lines.Count);
        // 両側に同じだけ余りがあれば（どちらも末尾改行で終わる）、その行どうしが揃うので足さない。
        var common = Math.Min(leftExtra, rightExtra);
        return (WithTrailingSpacers(left, rightExtra - common), WithTrailingSpacers(right, leftExtra - common));
    }

    /// <summary>行だけから組み立てる（エディタの行数が本文の行数どおりのとき）。</summary>
    public static (DiffEditorSide Left, DiffEditorSide Right) Build(IReadOnlyList<DiffSideRowVm> rows)
    {
        var (leftLines, rightLines) = SideLines(rows);
        return Build(rows, EditorLineCount(leftLines), EditorLineCount(rightLines));
    }

    /// <summary>
    /// 行ごとの行内差分（同じ高さに並んだ削除行と追加行の、変わった文字の範囲）。行数が多すぎる差分は取らない
    /// （構文色と同じ頭打ち——全行を書き換えた巨大ファイルで、UI スレッドの組み立てが重くならないように）。
    /// </summary>
    private static InlineChange?[] InlineChanges(
        IReadOnlyList<DiffSideRowVm> rows, Func<DiffSideRowVm, InlineChange?> inlineOf)
    {
        var result = new InlineChange?[rows.Count];
        if (rows.Count > DiffInlineHighlighter.MaxLines) return result;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i] is { LeftKind: "Removed", RightKind: "Added" })
                result[i] = inlineOf(rows[i]);
        return result;
    }

    /// <summary>
    /// 旧側の本文と、エディタで編集中の新側の本文から、行を取り直す（保存前の編集に差分を追従させる）。
    /// 新側は末尾改行を1つ落としてから比べる——git の行もそれを行として数えないため。
    /// </summary>
    public static List<DiffSideRowVm> Recompute(
        IReadOnlyList<string> leftLines, string rightText, DiffOptions? options = null)
    {
        var left = string.Join("\n", leftLines);
        var right = TrimOneTrailingNewline(rightText.Replace("\r\n", "\n"));
        var rows = new List<DiffSideRowVm>();
        foreach (var row in SideBySideDiff.Build(DiffUtil.ComputeFull(left, right, options)))
            rows.Add(new DiffSideRowVm(
                row.LeftKind.ToString(), row.LeftText, row.RightKind.ToString(), row.RightText,
                row.LeftLine?.ToString() ?? "", row.RightLine?.ToString() ?? ""));
        return rows;
    }

    /// <summary>
    /// エディタの本文が、行から取り出した本文と同じか。エディタ側の末尾の空行1つ（末尾改行）は同じとみなす。
    /// </summary>
    public static bool SameText(IReadOnlyList<string> expectedLines, string editorText)
    {
        var text = editorText.Replace("\r\n", "\n");
        var expected = string.Join("\n", expectedLines);
        return text == expected || TrimOneTrailingNewline(text) == expected;
    }

    /// <summary>その側のバッファ行（0始まり）が載っている行の添字。無ければ -1。</summary>
    public static int RowOfLine(IReadOnlyList<DiffSideRowVm> rows, int bufferLine, bool left)
    {
        var target = (bufferLine + 1).ToString();
        for (var i = 0; i < rows.Count; i++)
            if ((left ? rows[i].LeftLine : rows[i].RightLine) == target)
                return i;
        return -1;
    }

    /// <summary>行の添字 → その側のバッファ行（0始まり）。その側に無い行なら直前の行、先頭より前なら 0。</summary>
    public static int LineOfRow(IReadOnlyList<DiffSideRowVm> rows, int rowIndex, bool left)
        => Math.Max(0, DiffRowLineMapper.LineForSideRow(rows, rowIndex, left) - 1);

    private static DiffEditorSide BuildSide(IReadOnlyList<DiffSideRowVm> rows, InlineChange?[] inline, bool left)
    {
        var lines = new List<string>();
        var kinds = new Dictionary<int, DiffDecorationKind>();
        var spacers = new Dictionary<int, int>();
        var changes = new Dictionary<int, IReadOnlyList<DiffInlineRange>>();
        var pending = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var (number, kind, text) = left
                ? (row.LeftLine, row.LeftKind, row.LeftText)
                : (row.RightLine, row.RightKind, row.RightText);
            if (!HasLine(number))
            {
                pending++;   // 反対側にだけ行がある（Empty）か、左右共通の見出し行
                continue;
            }
            if (pending > 0)
            {
                spacers[lines.Count] = pending;
                pending = 0;
            }
            if (kind == nameof(SideCellKind.Added)) kinds[lines.Count] = DiffDecorationKind.Added;
            else if (kind == nameof(SideCellKind.Removed)) kinds[lines.Count] = DiffDecorationKind.Removed;
            if (inline[index] is { } change && (left ? change.Left : change.Right) is { Count: > 0 } spans)
                changes[lines.Count] = ToRanges(spans, lines.Count == 0 ? BomLength(text) : 0);
            lines.Add(text);
        }
        if (pending > 0)
            spacers[lines.Count] = pending;
        // 空き行は反対側にだけある行：左の空き行は右で足された行、右の空き行は左で消えた行。スクロールバーは
        // 右にしか出さないので、これで削除だけの変更の位置も右のスクロールバーに出る。
        return new DiffEditorSide(lines, new DiffDecorations(kinds, spacers, changes)
        {
            SpacerKind = left ? DiffDecorationKind.Added : DiffDecorationKind.Removed,
        });
    }

    /// <summary>行内差分の範囲をエディタの範囲へ。1行目の BOM はエディタに載せない（<see cref="WithoutBom"/>）ぶん左へずらす。</summary>
    private static DiffInlineRange[] ToRanges(IReadOnlyList<TextSpan> spans, int shift)
    {
        var ranges = new List<DiffInlineRange>(spans.Count);
        foreach (var span in spans)
        {
            var start = Math.Max(0, span.Start - shift);
            var end = Math.Max(start, span.End - shift);
            if (end > start) ranges.Add(new DiffInlineRange(start, end - start));
        }
        return ranges.ToArray();
    }

    private static int BomLength(string text) => text.Length - text.TrimStart('\uFEFF').Length;

    private static DiffEditorSide WithTrailingSpacers(DiffEditorSide side, int count)
    {
        if (count <= 0) return side;
        var spacers = new Dictionary<int, int>(side.Decorations.SpacersBefore);
        // 行数ちょうどの番号は末尾扱い。すでに末尾の空き行があればそこへ足す。
        var key = side.Lines.Count;
        spacers[key] = spacers.GetValueOrDefault(key) + count;
        return side with
        {
            Decorations = new DiffDecorations(side.Decorations.Lines, spacers, side.Decorations.InlineChanges)
            {
                SpacerKind = side.Decorations.SpacerKind,
            },
        };
    }

    /// <summary>本文の行をエディタへ載せたときの行数（空文書も1行）。</summary>
    private static int EditorLineCount(IReadOnlyList<string> lines) => Math.Max(1, lines.Count);

    private static bool HasLine(string number) => number.Length > 0;

    /// <summary>git の出力は UTF-8 として読むので、BOM 付きファイルの1行目には BOM の文字が残る。
    /// エディタは BOM を剥がして読むので、残したままだと1行目が常に「違う」ことになる。</summary>
    private static string WithoutBom(string text) => text.TrimStart('\uFEFF');

    private static string TrimOneTrailingNewline(string text)
        => text.EndsWith('\n') ? text[..^1] : text;
}
