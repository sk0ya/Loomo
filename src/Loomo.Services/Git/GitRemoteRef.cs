using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>origin/feature/foo</c> のような「リモート名＋ブランチ名」の分解（純粋・git は起動しない）。
///
/// <para><b>単純に最初の "/" で切ってはいけない</b>——リモート名自体に "/" は使えないが、ブランチ名には
/// 使える（<c>feature/foo</c>）し、逆に<b>リモート名に一致しない</b>先頭要素はブランチ名の一部
/// （ローカル上流 <c>main</c> や、"/" を含むだけのローカルブランチ）。登録済みリモート名との
/// <b>最長一致</b>で判定するのが唯一正しい切り方で、取り違えると
/// <c>push origin/main</c> のような「origin/main という名前のローカルブランチ」を作ってしまう。</para>
/// </summary>
public static class GitRemoteRef
{
    /// <summary>
    /// <paramref name="reference"/> を「登録済みリモート名＋その先のブランチ名」に分解する。
    /// どのリモート名にも前置一致しなければ null（＝リモート追跡ではない）。
    /// </summary>
    public static (string Remote, string Branch)? TrySplit(
        string? reference, IEnumerable<string> remotes)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var value = reference.Trim();
        var remote = remotes?
            .Where(name => !string.IsNullOrEmpty(name)
                && value.StartsWith(name + "/", StringComparison.Ordinal))
            .OrderByDescending(name => name.Length)
            .FirstOrDefault();
        if (remote is null)
            return null;

        var branch = value[(remote.Length + 1)..];
        return branch.Length == 0 ? null : (remote, branch);
    }
}
