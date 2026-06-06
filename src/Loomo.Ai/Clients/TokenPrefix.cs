using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>トークン列のユーティリティ。ターン間 KV キャッシュ再利用で「共通接頭辞」を求めるのに使う。</summary>
public static class TokenPrefix
{
    /// <summary>
    /// 2つのトークン列の先頭から一致する長さ（最長共通接頭辞の長さ）を返す。
    /// ターン間で前回フィード済みのトークン列と今回のプロンプトを比べ、一致する分は KV を再利用し、
    /// 分岐した分だけ再 prefill するために使う。
    /// </summary>
    public static int CommonLength(IReadOnlyList<int> a, ReadOnlySpan<int> b)
    {
        var n = Math.Min(a.Count, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }
}
