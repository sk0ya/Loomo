using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>
/// HEAD↔作業ツリーの差分（人が見る1枚の差分）の変更行が、ステージ済みかどうかを引く対応表。UI 非依存・純粋。
///
/// <para>ステージしても差分から行が消えないようにするには、表示は HEAD↔作業ツリーの1枚にして、
/// 各変更行が「どこまで進んでいるか」を別に知る必要がある。それを git の2つの差分——HEAD↔インデックス
/// （ステージ済み）とインデックス↔作業ツリー（未ステージ）——から組み立てる：</para>
/// <list type="bullet">
/// <item>削除行（HEAD の行 h）：HEAD↔インデックスで h が削除されていればステージ済み。</item>
/// <item>追加行（作業ツリーの行 w）：インデックス↔作業ツリーで w が追加されていれば未ステージ。そうでなければ
/// w に当たるインデックスの行 i が HEAD↔インデックスで追加されている＝ステージ済み。</item>
/// </list>
/// <para>ステージ／アンステージ／破棄のパッチは、それぞれの差分の行番号で作る必要があるので、HEAD・作業ツリーの
/// 行番号をインデックスの行番号へ写す（<see cref="Split"/>）。</para>
/// </summary>
public sealed class StagedChangeMap
{
    private readonly PatchLineMap _staged;     // 旧=HEAD, 新=インデックス
    private readonly PatchLineMap _unstaged;   // 旧=インデックス, 新=作業ツリー

    private StagedChangeMap(PatchLineMap staged, PatchLineMap unstaged)
    {
        _staged = staged;
        _unstaged = unstaged;
    }

    /// <param name="stagedPatch">HEAD↔インデックスの差分（<c>git diff --cached</c>）。</param>
    /// <param name="unstagedPatch">インデックス↔作業ツリーの差分（<c>git diff</c>）。</param>
    public static StagedChangeMap Build(string stagedPatch, string unstagedPatch)
        => new(PatchLineMap.Parse(stagedPatch), PatchLineMap.Parse(unstagedPatch));

    /// <summary>HEAD の行 <paramref name="headLine"/>（1始まり）の削除がステージ済みか。</summary>
    public bool IsStagedRemoval(int headLine) => _staged.Removed.Contains(headLine);

    /// <summary>作業ツリーの行 <paramref name="worktreeLine"/>（1始まり）の追加がステージ済みか。</summary>
    public bool IsStagedAddition(int worktreeLine)
        => !_unstaged.Added.Contains(worktreeLine)
           && _unstaged.NewToOld(worktreeLine) is { } indexLine
           && _staged.Added.Contains(indexLine);

    /// <summary>
    /// HEAD↔作業ツリーの差分で選んだ変更行（削除＝HEAD の行番号、追加＝作業ツリーの行番号）を、
    /// 未ステージのもの（インデックス↔作業ツリーの行番号へ）とステージ済みのもの（HEAD↔インデックスの行番号へ）に分ける。
    /// どちらの差分にも対応が見つからない行（表示の差分と2つの差分で行の合わせ方が食い違ったとき）は落とす。
    /// </summary>
    public StageSplit Split(IEnumerable<int> headLines, IEnumerable<int> worktreeLines)
    {
        var unstagedOld = new HashSet<int>();
        var unstagedNew = new HashSet<int>();
        var stagedOld = new HashSet<int>();
        var stagedNew = new HashSet<int>();

        foreach (var head in headLines)
        {
            if (_staged.Removed.Contains(head))
                stagedOld.Add(head);
            else if (_staged.OldToNew(head) is { } index && _unstaged.Removed.Contains(index))
                unstagedOld.Add(index);
        }
        foreach (var worktree in worktreeLines)
        {
            if (_unstaged.Added.Contains(worktree))
                unstagedNew.Add(worktree);
            else if (_unstaged.NewToOld(worktree) is { } index && _staged.Added.Contains(index))
                stagedNew.Add(index);
        }
        return new StageSplit(
            new PatchLineSelection(unstagedOld, unstagedNew),
            new PatchLineSelection(stagedOld, stagedNew));
    }

    /// <summary>1つのパッチの行番号での選択（旧側＝削除行、新側＝追加行）。</summary>
    public sealed record PatchLineSelection(IReadOnlySet<int> OldLines, IReadOnlySet<int> NewLines)
    {
        public bool IsEmpty => OldLines.Count == 0 && NewLines.Count == 0;
    }

    /// <param name="Unstaged">インデックス↔作業ツリーの行番号（ステージ・破棄に使う）。</param>
    /// <param name="Staged">HEAD↔インデックスの行番号（アンステージに使う）。</param>
    public sealed record StageSplit(PatchLineSelection Unstaged, PatchLineSelection Staged);

    /// <summary>1つの unified diff の、変更行の集合と、変わっていない行の旧↔新の行番号の対応。</summary>
    private sealed class PatchLineMap
    {
        private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

        private readonly List<Hunk> _hunks = new();
        public HashSet<int> Removed { get; } = new();
        public HashSet<int> Added { get; } = new();

        private sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount,
            Dictionary<int, int> OldToNew, Dictionary<int, int> NewToOld);

        public static PatchLineMap Parse(string patchText)
        {
            var map = new PatchLineMap();
            var lines = patchText.Replace("\r\n", "\n").Split('\n');
            var index = 0;
            while (index < lines.Length)
            {
                var match = HunkHeader.Match(lines[index++]);
                if (!match.Success) continue;
                var oldStart = int.Parse(match.Groups[1].Value);
                var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                var newStart = int.Parse(match.Groups[3].Value);
                var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                var hunk = new Hunk(oldStart, oldCount, newStart, newCount, new(), new());
                int oldLine = oldStart, newLine = newStart;
                while (index < lines.Length && !lines[index].StartsWith("@@", StringComparison.Ordinal))
                {
                    var line = lines[index++];
                    if (line.Length == 0 || line[0] == '\\') continue;
                    switch (line[0])
                    {
                        case '-': map.Removed.Add(oldLine++); break;
                        case '+': map.Added.Add(newLine++); break;
                        case ' ':
                            hunk.OldToNew[oldLine] = newLine;
                            hunk.NewToOld[newLine] = oldLine;
                            oldLine++;
                            newLine++;
                            break;
                    }
                }
                map._hunks.Add(hunk);
            }
            return map;
        }

        public int? OldToNew(int line) => Map(line, fromOld: true);
        public int? NewToOld(int line) => Map(line, fromOld: false);

        /// <summary>変わっていない行を反対側の行番号へ写す（変更行なら null）。ハンクの外は、前にあるハンクの
        /// 増減ぶんずらすだけ。</summary>
        private int? Map(int line, bool fromOld)
        {
            var delta = 0;
            foreach (var hunk in _hunks)
            {
                var (fromStart, fromCount, toStart, toCount, inside) = fromOld
                    ? (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, hunk.OldToNew)
                    : (hunk.NewStart, hunk.NewCount, hunk.OldStart, hunk.OldCount, hunk.NewToOld);
                // 長さ0の側（純粋な追加／削除）の開始番号は「この行の後ろ」を指すので、ハンクは次の行から。
                var first = fromCount == 0 ? fromStart + 1 : fromStart;
                if (line < first) break;
                if (line < first + fromCount)
                    return inside.TryGetValue(line, out var mapped) ? mapped : null;
                var toFirst = toCount == 0 ? toStart + 1 : toStart;
                delta = toFirst + toCount - (first + fromCount);
            }
            return line + delta;
        }
    }
}
