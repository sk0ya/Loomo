using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>コード整理で何をするか（Rider の Code Cleanup profile に当たる）。</summary>
public enum CSharpCleanupProfile
{
    /// <summary>using 整理（意味解析が使えれば未使用の削除も）＋整形＋行末空白・改行・末尾改行。</summary>
    Full,

    /// <summary>整形とレイアウト（行末空白・改行・末尾改行）だけ。using には触らない。</summary>
    Reformat,
}

/// <summary>コード整理の結果を、指定した行だけに絞る純ロジック（Rider の「選択範囲を整理」
/// 「VCS で変更したテキストだけ整形」に当たる）。
///
/// <para>整理そのものは常にファイル全体で行い（Roslyn の整形も using 整理もファイル単位でしか正しく
/// 判定できない）、その結果と元の本文の行差分のうち、元の行が対象に入るかたまりだけを採る。
/// 削除と追加が同数のかたまり（整形でよくある「行の中だけ変わった」）は行ごとに分けて判定する——
/// まとめて判定すると、選択の外の行まで一緒に書き換わる。</para>
/// </summary>
public static class CSharpCleanupScope
{
    public static CSharpCleanupOptions OptionsFor(
        CSharpCleanupProfile profile, CSharpCleanupOptions editorConfigOptions)
    {
        ArgumentNullException.ThrowIfNull(editorConfigOptions);
        return profile switch
        {
            CSharpCleanupProfile.Reformat => editorConfigOptions with
            {
                OrganizeUsings = false,
                RemoveUnusedUsings = false,
                Format = true,
            },
            _ => editorConfigOptions with { OrganizeUsings = true, Format = true },
        };
    }

    /// <summary>選択範囲（0 始まり・終わりの列が 0 ならその行は含めない＝行選択の Shift+↓ と同じ）を行の集合へ。</summary>
    public static IReadOnlySet<int> LinesOfSelection(int startLine, int endLine, int endCharacter)
    {
        if (endLine < startLine) (startLine, endLine) = (endLine, startLine);
        var last = endCharacter == 0 && endLine > startLine ? endLine - 1 : endLine;
        return Enumerable.Range(startLine, last - startLine + 1).ToHashSet();
    }

    /// <summary><paramref name="baseline"/>（HEAD など）から <paramref name="current"/> へ変わった行
    /// （<paramref name="current"/> 側の 0 始まり）。<paramref name="baseline"/> が null（未追跡・Git 外）なら全行。</summary>
    public static IReadOnlySet<int> ChangedLines(string? baseline, string current)
    {
        var lines = new HashSet<int>();
        if (baseline is null)
        {
            var count = Split(current).Length;
            for (var i = 0; i < count; i++) lines.Add(i);
            return lines;
        }

        var index = 0;
        foreach (var line in DiffUtil.ComputeFull(baseline, current))
        {
            switch (line.Kind)
            {
                case DiffLineKind.Added:
                    lines.Add(index++);
                    break;
                case DiffLineKind.Context:
                    index++;
                    break;
            }
        }
        return lines;
    }

    /// <summary><paramref name="cleaned"/> の変更のうち、<paramref name="original"/> の
    /// <paramref name="lines"/>（0 始まり）に掛かるものだけを当てた本文を返す。</summary>
    public static string Restrict(string original, string cleaned, IReadOnlySet<int> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.Equals(original, cleaned, StringComparison.Ordinal)) return original;

        var diff = DiffUtil.ComputeFull(original, cleaned);
        var result = new List<string>(diff.Count);
        var removed = new List<(int Line, string Text)>();
        var added = new List<string>();
        var oldIndex = 0;

        void Flush()
        {
            if (removed.Count == 0 && added.Count == 0) return;
            if (removed.Count == added.Count)
            {
                // 行の中だけ変わったかたまり：1 行ずつ決める。
                for (var i = 0; i < removed.Count; i++)
                    result.Add(lines.Contains(removed[i].Line) ? added[i] : removed[i].Text);
            }
            else
            {
                // 行数が変わるかたまり：元の行が 1 つでも対象なら採る。純粋な挿入は、挟まれた前後の行で決める。
                var take = removed.Count > 0
                    ? removed.Any(line => lines.Contains(line.Line))
                    : lines.Contains(oldIndex - 1) || lines.Contains(oldIndex);
                if (take) result.AddRange(added);
                else result.AddRange(removed.Select(line => line.Text));
            }
            removed.Clear();
            added.Clear();
        }

        foreach (var line in diff)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Removed:
                    removed.Add((oldIndex++, line.Text));
                    break;
                case DiffLineKind.Added:
                    added.Add(line.Text);
                    break;
                default:
                    Flush();
                    result.Add(line.LeftText);
                    oldIndex++;
                    break;
            }
        }
        Flush();

        // 触らなかった行の改行コードを変えないよう、元の本文の改行で綴じる。
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Join(newline, result);
    }

    private static string[] Split(string text)
        => text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
