using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>選んだ変更行だけに縮約したパッチと、そこに変更として残した行数。</summary>
public readonly record struct SelectedLinesPatch(string Patch, int LineCount)
{
    /// <summary>選んだ変更が1行も無い（適用すべきものが無い）か。</summary>
    public bool IsEmpty => LineCount == 0 || Patch.Length == 0;
}

/// <summary>
/// unified diff（git のファイル差分パッチ）から、ユーザーが選んだ変更行だけを「逆適用で破棄」できる
/// 縮約パッチを作る。UI 非依存・純粋関数（テスト可能）。
///
/// 作業ツリーの一部の行だけを破棄する定石（git-gui / Magit と同じ）：選んだ <c>+</c>/<c>-</c> 行だけを
/// 変更として残し、残す <c>+</c> 行は文脈行へ変換、残す <c>-</c> 行は丸ごと落とす。これを
/// <c>git apply --reverse --recount</c> で作業ツリーへ逆適用すると、選んだ行だけが取り消される。
/// ハンク見出しの行数は壊れるが <c>--recount</c> が再計算するので問題ない。
///
/// 選んだ行だけを<b>ステージ</b>するのは向きが逆になる（パッチの旧側＝インデックスへ順適用する）：
/// 残す <c>-</c> 行はインデックスにまだあるので文脈行へ、残す <c>+</c> 行はインデックスに無いので丸ごと落とす。
/// 選んだ行だけを<b>アンステージ</b>するのは、ステージ済みの差分（HEAD↔インデックス）に対する破棄と同じ形——
/// 縮約は <see cref="BuildReverseDiscardPatch"/> のまま、適用先がインデックスになるだけ。
/// </summary>
public static class UnifiedPatchEditor
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    /// <summary>本文1行の文脈：パッチ全体での行インデックスと、その行の旧/新ファイル行番号（1始まり）。</summary>
    private readonly record struct BodyLine(int GlobalIndex, char Marker, int OldLine, int NewLine);

    /// <summary>
    /// <paramref name="patchText"/>（1ファイル分の unified diff）から、<paramref name="selectedLineIndices"/>
    /// （パッチを改行で分割した0始まりの行インデックス。表示中の差分行と1対1）に含まれる <c>+</c>/<c>-</c>
    /// 行だけを破棄対象に残した、逆適用用パッチを返す。統合（unified）表示の行選択に使う。
    /// </summary>
    public static SelectedLinesPatch BuildReverseDiscardPatch(
        string patchText, IReadOnlySet<int> selectedLineIndices)
        => Build(patchText, body => selectedLineIndices.Contains(body.GlobalIndex));

    /// <summary>
    /// 旧ファイルの行番号集合（復活させる削除行）と新ファイルの行番号集合（取り消す追加行）で破棄対象を選び、
    /// 逆適用用パッチを返す。左右並び表示で変更ブロック（範囲）をまとめて破棄するのに使う。
    /// </summary>
    public static SelectedLinesPatch BuildReverseDiscardPatchForLines(
        string patchText, IReadOnlySet<int> oldLinesToRestore, IReadOnlySet<int> newLinesToRemove)
        => Build(patchText, body => body.Marker == '+'
            ? newLinesToRemove.Contains(body.NewLine)
            : oldLinesToRestore.Contains(body.OldLine));

    /// <summary>
    /// パッチの各行（改行で分割した0始まりの行インデックス順）の変更の印と、旧/新ファイルでの行番号（1始まり）。
    /// <c>+</c>/<c>-</c> 以外の行の <see cref="PatchLine.Marker"/> は <c>'\0'</c>。統合表示の行選択を、
    /// 行番号での選択（左右並びと同じ形）へ直すのに使う。
    /// </summary>
    public static IReadOnlyList<PatchLine> DescribeLines(string patchText)
    {
        var lines = patchText.Replace("\r\n", "\n").Split('\n');
        var result = new PatchLine[lines.Length];
        int oldLine = 0, newLine = 0;
        var inHunk = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = HunkHeader.Match(line);
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value);
                newLine = int.Parse(match.Groups[2].Value);
                inHunk = true;
                continue;
            }
            if (!inHunk || line.Length == 0 || line[0] == '\\')
                continue;
            switch (line[0])
            {
                case '+': result[i] = new PatchLine('+', 0, newLine++); break;
                case '-': result[i] = new PatchLine('-', oldLine++, 0); break;
                case ' ': oldLine++; newLine++; break;
                default: inHunk = false; break;
            }
        }
        return result;
    }

    /// <summary><see cref="DescribeLines"/> の1行。<see cref="OldLine"/> は削除行、<see cref="NewLine"/> は追加行で意味を持つ。</summary>
    public readonly record struct PatchLine(char Marker, int OldLine, int NewLine);

    /// <summary>
    /// <paramref name="selectedLineIndices"/>（<see cref="BuildReverseDiscardPatch"/> と同じ行インデックス）の
    /// <c>+</c>/<c>-</c> 行だけを、インデックスへ<b>順適用</b>（<c>git apply --cached --recount</c>）してステージする
    /// 縮約パッチを返す。統合表示の行選択に使う。
    /// </summary>
    public static SelectedLinesPatch BuildStagePatch(
        string patchText, IReadOnlySet<int> selectedLineIndices)
        => Build(patchText, body => selectedLineIndices.Contains(body.GlobalIndex), forward: true);

    /// <summary>
    /// 旧/新ファイルの行番号集合で選んだ変更だけをステージする縮約パッチを返す。左右並び表示の変更ブロック用。
    /// </summary>
    public static SelectedLinesPatch BuildStagePatchForLines(
        string patchText, IReadOnlySet<int> oldLines, IReadOnlySet<int> newLines)
        => Build(patchText, body => body.Marker == '+'
            ? newLines.Contains(body.NewLine)
            : oldLines.Contains(body.OldLine), forward: true);

    /// <summary>
    /// 共通の組み立て。<paramref name="isSelected"/> は <c>+</c>/<c>-</c> 本文行に対してのみ呼ばれ、
    /// true の行を変更として残す。<paramref name="forward"/> はパッチの適用向き：false（逆適用＝破棄・アンステージ）
    /// なら適用先は新側なので、残す <c>+</c> を文脈行へ・残す <c>-</c> を落とす。true（順適用＝ステージ）なら
    /// 適用先は旧側なので、残す <c>-</c> を文脈行へ・残す <c>+</c> を落とす。
    /// </summary>
    private static SelectedLinesPatch Build(
        string patchText, Func<BodyLine, bool> isSelected, bool forward = false)
    {
        var lines = patchText.Replace("\r\n", "\n").Split('\n');
        var n = lines.Length;

        // 先頭のハンク見出し（@@）位置を探す。それより前は git のファイルヘッダ。
        var firstHunk = -1;
        for (var i = 0; i < n; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal)) { firstHunk = i; break; }
        if (firstHunk < 0) return new SelectedLinesPatch("", 0); // ハンクが無い（合成パッチ等）

        // ファイルヘッダ（diff --git / index / --- / +++ / new file ...）だけを集める（# コメント等は除く）。
        var header = new List<string>();
        for (var i = 0; i < firstHunk; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (SideBySideDiff.ClassifyPatchLine(line) == SideCellKind.Header)
                header.Add(line);
        }

        var outHunks = new List<string>();
        var selected = 0;
        var idx = firstHunk;
        while (idx < n)
        {
            var match = HunkHeader.Match(lines[idx]);
            if (!match.Success) { idx++; continue; }
            var hunkHeader = lines[idx];
            var oldLine = int.Parse(match.Groups[1].Value);
            var newLine = int.Parse(match.Groups[2].Value);
            idx++;

            var transformed = new List<string>();
            var count = 0;
            var previousDropped = false;
            while (idx < n && !lines[idx].StartsWith("@@", StringComparison.Ordinal))
            {
                var line = lines[idx];
                idx++;
                if (line.Length == 0)
                    continue; // 末尾の空要素など（文脈行は必ず先頭に空白が付く）

                if (line.StartsWith("\\", StringComparison.Ordinal))
                {
                    // 「\ No newline at end of file」マーカー：直前の行を残したときだけ残す
                    if (!previousDropped) transformed.Add(line);
                    continue;
                }

                var marker = line[0];
                if (marker is '+' or '-')
                {
                    var body = new BodyLine(idx - 1, marker, oldLine, newLine);
                    if (isSelected(body))
                    {
                        // 選んだ変更：+/- のまま残す（逆適用なら作業ツリーから消える／復活する、順適用ならステージされる）
                        transformed.Add(line);
                        count++;
                        previousDropped = false;
                    }
                    else if ((marker == '+') == forward)
                    {
                        previousDropped = true; // 適用先に無い側の行（逆適用の -、順適用の +）：丸ごと落とす
                    }
                    else
                    {
                        transformed.Add(' ' + line[1..]); // 適用先にある側の行：文脈行へ変換
                        previousDropped = false;
                    }
                    if (marker == '+') newLine++;
                    else oldLine++;
                }
                else
                {
                    transformed.Add(line); // 文脈行（先頭空白）はそのまま
                    previousDropped = false;
                    oldLine++;
                    newLine++;
                }
            }

            if (count == 0) continue; // 選択された変更がこのハンクに無ければ丸ごと捨てる
            selected += count;
            outHunks.Add(hunkHeader);
            outHunks.AddRange(transformed);
        }

        if (outHunks.Count == 0) return new SelectedLinesPatch("", 0);

        var sb = new StringBuilder();
        foreach (var line in header) sb.Append(line).Append('\n');
        foreach (var line in outHunks) sb.Append(line).Append('\n');
        return new SelectedLinesPatch(sb.ToString(), selected);
    }
}
