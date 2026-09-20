using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 1ファイル分の unified diff（git diff 出力）を、ファイルヘッダ（diff --git / index / --- / +++）と
/// 各ハンク（@@ で始まるブロック）に分解する。ハンク単位のステージ／アンステージで、選択した1ハンク
/// だけの最小パッチを組み立てて <c>git apply --cached</c> へ渡すために使う（純ロジック・テスト可能）。
/// </summary>
public static class GitPatchSplitter
{
    /// <summary>1ハンク。<see cref="HeaderLine"/> は @@ 行、<see cref="Text"/> は @@ 行＋本文（末尾改行つき）。</summary>
    public sealed record Hunk(string HeaderLine, string Text);

    /// <summary>分解結果。<see cref="Header"/> は最初の @@ より前（ファイルヘッダ。末尾改行つき）。</summary>
    public sealed record SplitPatch(string Header, IReadOnlyList<Hunk> Hunks);

    public static SplitPatch Split(string patch)
    {
        var norm = patch.Replace("\r\n", "\n");
        var lines = norm.Split('\n');

        var firstHunk = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                firstHunk = i;
                break;
            }
        }

        if (firstHunk < 0)
            return new SplitPatch(EnsureTrailingNewline(norm), Array.Empty<Hunk>());

        var header = EnsureTrailingNewline(string.Join("\n", lines[..firstHunk]));

        var hunks = new List<Hunk>();
        var index = firstHunk;
        while (index < lines.Length)
        {
            if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var start = index;
            index++;
            while (index < lines.Length && !lines[index].StartsWith("@@", StringComparison.Ordinal))
                index++;

            var body = string.Join("\n", lines[start..index]);
            hunks.Add(new Hunk(lines[start], EnsureTrailingNewline(body)));
        }

        return new SplitPatch(header, hunks);
    }

    /// <summary>ファイルヘッダ＋1ハンクの最小パッチを組み立てる（git apply --cached に渡せる形）。</summary>
    public static string BuildSingleHunkPatch(string header, Hunk hunk)
        => EnsureTrailingNewline(header) + EnsureTrailingNewline(hunk.Text);

    private static string EnsureTrailingNewline(string text)
    {
        if (text.Length == 0)
            return text;
        return text.EndsWith('\n') ? text : text + "\n";
    }
}
