using System;
using System.Collections.Generic;
using System.Text;

namespace sk0ya.Loomo.Ai.Completion;

/// <summary>
/// FIM（fill-in-the-middle）のプロンプト組み立て。Qwen2.5-Coder の
/// <c>&lt;|fim_prefix|&gt; … &lt;|fim_suffix|&gt; … &lt;|fim_middle|&gt;</c> 形式。
///
/// <para><b>並びは PSM（prefix→suffix→middle）で固定。</b>後ろの本文を先頭へ回す SPM 順にすると
/// KV キャッシュの再利用率が上がる（打鍵で変わるのが末尾だけになる）ので一度試したが、
/// 実測では 6 問すべて外した——手掛かりを無視して無関係な行を書き出す。Qwen2.5-Coder は
/// この並びを学習していないと見るのが妥当で、速度のために正しさを捨てることはできない。</para>
/// </summary>
public static class FimPrompt
{
    public const string PrefixToken = "<|fim_prefix|>";
    public const string SuffixToken = "<|fim_suffix|>";
    public const string MiddleToken = "<|fim_middle|>";

    /// <summary>生成を止める印。モデルが 1 行を書き終えたか、別ファイルへ話を移そうとした合図。</summary>
    public static readonly string[] StopMarkers =
        ["\n", "<|endoftext|>", "<|fim_pad|>", "<|file_sep|>", "<|repo_name|>", PrefixToken, SuffixToken, MiddleToken];

    /// <summary>
    /// キャレット位置を挟んで前後を切り出し、FIM プロンプトを組む。
    /// </summary>
    /// <param name="lines">バッファ全行。</param>
    /// <param name="line">キャレット行（0 始まり）。</param>
    /// <param name="column">キャレット桁（0 始まり）。</param>
    /// <param name="prefixLines">前に見せる行数。ここは KV が効くので長くても打鍵ごとの費用は増えない。</param>
    /// <param name="suffixLines">後ろに見せる行数。<b>打鍵のたびに送り直しになるので短く。</b></param>
    public static string Build(
        IReadOnlyList<string> lines, int line, int column, int prefixLines, int suffixLines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return PrefixToken + SuffixToken + MiddleToken;

        line = Math.Clamp(line, 0, lines.Count - 1);
        var current = lines[line];
        column = Math.Clamp(column, 0, current.Length);

        var builder = new StringBuilder(256);
        builder.Append(PrefixToken);

        int firstLine = Math.Max(0, line - Math.Max(0, prefixLines));
        for (int i = firstLine; i < line; i++)
        {
            builder.Append(lines[i]);
            builder.Append('\n');
        }
        builder.Append(current, 0, column);

        builder.Append(SuffixToken);
        builder.Append(current, column, current.Length - column);

        int lastLine = Math.Min(lines.Count - 1, line + Math.Max(0, suffixLines));
        for (int i = line + 1; i <= lastLine; i++)
        {
            builder.Append('\n');
            builder.Append(lines[i]);
        }

        builder.Append(MiddleToken);
        return builder.ToString();
    }
}
