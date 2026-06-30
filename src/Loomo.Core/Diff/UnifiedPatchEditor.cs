using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>縮約した逆適用パッチと、そこに含めた破棄対象行数。</summary>
public readonly record struct ReverseDiscardPatch(string Patch, int DiscardedLineCount)
{
    /// <summary>破棄対象が1行も無い（適用すべきものが無い）か。</summary>
    public bool IsEmpty => DiscardedLineCount == 0 || Patch.Length == 0;
}

/// <summary>
/// unified diff（git のファイル差分パッチ）から、ユーザーが選んだ変更行だけを「逆適用で破棄」できる
/// 縮約パッチを作る。UI 非依存・純粋関数（テスト可能）。
///
/// 作業ツリーの一部の行だけを破棄する定石（git-gui / Magit と同じ）：選んだ <c>+</c>/<c>-</c> 行だけを
/// 変更として残し、残す <c>+</c> 行は文脈行へ変換、残す <c>-</c> 行は丸ごと落とす。これを
/// <c>git apply --reverse --recount</c> で作業ツリーへ逆適用すると、選んだ行だけが取り消される。
/// ハンク見出しの行数は壊れるが <c>--recount</c> が再計算するので問題ない。
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
    public static ReverseDiscardPatch BuildReverseDiscardPatch(
        string patchText, IReadOnlySet<int> selectedLineIndices)
        => Build(patchText, body => selectedLineIndices.Contains(body.GlobalIndex));

    /// <summary>
    /// 旧ファイルの行番号集合（復活させる削除行）と新ファイルの行番号集合（取り消す追加行）で破棄対象を選び、
    /// 逆適用用パッチを返す。左右並び表示で変更ブロック（範囲）をまとめて破棄するのに使う。
    /// </summary>
    public static ReverseDiscardPatch BuildReverseDiscardPatchForLines(
        string patchText, IReadOnlySet<int> oldLinesToRestore, IReadOnlySet<int> newLinesToRemove)
        => Build(patchText, body => body.Marker == '+'
            ? newLinesToRemove.Contains(body.NewLine)
            : oldLinesToRestore.Contains(body.OldLine));

    /// <summary>
    /// 共通の組み立て。<paramref name="isDiscarded"/> は <c>+</c>/<c>-</c> 本文行に対してのみ呼ばれ、
    /// true の行を破棄対象として残す。
    /// </summary>
    private static ReverseDiscardPatch Build(string patchText, Func<BodyLine, bool> isDiscarded)
    {
        var lines = patchText.Replace("\r\n", "\n").Split('\n');
        var n = lines.Length;

        // 先頭のハンク見出し（@@）位置を探す。それより前は git のファイルヘッダ。
        var firstHunk = -1;
        for (var i = 0; i < n; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal)) { firstHunk = i; break; }
        if (firstHunk < 0) return new ReverseDiscardPatch("", 0); // ハンクが無い（合成パッチ等）

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
        var discarded = 0;
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
                if (marker == '+')
                {
                    if (isDiscarded(new BodyLine(idx - 1, '+', oldLine, newLine)))
                    {
                        transformed.Add(line); // 破棄対象：追加行のまま（逆適用で作業ツリーから消える）
                        count++;
                    }
                    else
                    {
                        transformed.Add(' ' + line[1..]); // 残す追加：文脈行へ変換
                    }
                    previousDropped = false;
                    newLine++;
                }
                else if (marker == '-')
                {
                    if (isDiscarded(new BodyLine(idx - 1, '-', oldLine, newLine)))
                    {
                        transformed.Add(line); // 破棄対象：削除行のまま（逆適用で作業ツリーへ復活）
                        count++;
                        previousDropped = false;
                    }
                    else
                    {
                        previousDropped = true; // 残す削除：丸ごと落とす（復活させない）
                    }
                    oldLine++;
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
            discarded += count;
            outHunks.Add(hunkHeader);
            outHunks.AddRange(transformed);
        }

        if (outHunks.Count == 0) return new ReverseDiscardPatch("", 0);

        var sb = new StringBuilder();
        foreach (var line in header) sb.Append(line).Append('\n');
        foreach (var line in outHunks) sb.Append(line).Append('\n');
        return new ReverseDiscardPatch(sb.ToString(), discarded);
    }
}
