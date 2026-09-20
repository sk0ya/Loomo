using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git submodule status</c> の出力をパースする。1行は「状態フラグ(1桁)＋ハッシュ(40桁)＋空白＋パス」
/// で、末尾に <c>(describe)</c> が付くことがある。フラグは正常時は空白、<c>-</c>=未初期化、
/// <c>+</c>=登録コミットと不一致、<c>U</c>=マージ未解決。<c>.gitmodules</c> が無い／サブモジュールが
/// 無いリポジトリでは出力が空になり、その場合は空リストを返す（呼び出し側でのエラー扱いは不要）。
/// </summary>
public static class GitSubmoduleParser
{
    /// <summary>状態フラグ(1) + ハッシュ(40) の最小長。</summary>
    private const int HashFieldLength = 41;

    public static IReadOnlyList<GitSubmoduleInfo> Parse(string output)
    {
        var result = new List<GitSubmoduleInfo>();
        foreach (var line in output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length <= HashFieldLength)
                continue;

            var statusChar = l[0];
            var hash = l.Substring(1, 40);
            var rest = l[HashFieldLength..].TrimStart(' ');
            if (rest.Length == 0)
                continue;

            string path;
            string? describe = null;
            if (rest.EndsWith(')'))
            {
                var open = rest.LastIndexOf(" (", StringComparison.Ordinal);
                if (open >= 0)
                {
                    path = rest[..open];
                    describe = rest[(open + 2)..^1];
                }
                else
                {
                    path = rest;
                }
            }
            else
            {
                path = rest;
            }

            result.Add(new GitSubmoduleInfo(
                path,
                hash,
                describe,
                IsUninitialized: statusChar == '-',
                HasDivergedCommit: statusChar == '+',
                HasMergeConflict: statusChar == 'U'));
        }
        return result;
    }
}
