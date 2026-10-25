using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>
/// プッシュ済みのコミットを書き換える前に、そうだと言う（§24.78・Emacs 31.1 の <c>vc-allow-rewriting-published-history</c>）。
/// 「コミットから外す」「メッセージを修正」「スカッシュ」「リベース」「amend」の確認は「履歴が書き換わります」と一律に言うだけで、
/// それが<b>もう人の手元にある履歴か</b>を見ていなかった。止めはしない（強制プッシュの口は既にある）——確認文の先頭で言い、重さを上げる。
/// </summary>
public static class GitPublishedHistory
{
    /// <summary>名前を並べる上限。超えた分は「ほか N 件」。</summary>
    public const int MaxNames = 3;

    /// <summary><c>git branch -r --contains --format=%(refname:short)</c> の出力を読む。<c>origin/HEAD</c> のような
    /// 別名と、ブランチを指していない行（<c>(HEAD detached …)</c>）は数えない。</summary>
    public static IReadOnlyList<string> ParseRemoteBranches(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !name.StartsWith('(') && !name.EndsWith("/HEAD", StringComparison.Ordinal) && !name.Contains(" -> "))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>確認文の先頭に置く一文。プッシュされていなければ null。</summary>
    public static string? Describe(IReadOnlyList<string> remoteBranches)
    {
        if (remoteBranches.Count == 0)
            return null;
        var names = string.Join("、", remoteBranches.Take(MaxNames));
        if (remoteBranches.Count > MaxNames)
            names += $" ほか {remoteBranches.Count - MaxNames} 件";
        return $"⚠ このコミットは {names} にプッシュ済みです。書き換えると強制プッシュが要り、" +
               "同じブランチを取り込んだ人の手元とずれます。";
    }

    /// <summary>確認文 <paramref name="message"/> の先頭へ <see cref="Describe"/> を添える。</summary>
    public static string Prefix(string? warning, string message)
        => warning is null ? message : warning + "\n\n" + message;
}
