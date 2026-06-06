using sk0ya.Loomo.Ai.Clients;

namespace sk0ya.Loomo.Tests;

/// <summary>ターン間 KV 再利用の共通接頭辞計算（<see cref="TokenPrefix.CommonLength"/>）のテスト。</summary>
public class TokenPrefixTests
{
    [Fact]
    public void Returns_full_length_when_one_is_prefix_of_other()
    {
        // 前回フィード [1,2,3] に対し今回 [1,2,3,4,5] → 共通は 3（[1,2,3] 再利用、[4,5] のみ再 prefill）。
        Assert.Equal(3, TokenPrefix.CommonLength(new[] { 1, 2, 3 }, new[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void Stops_at_first_divergence()
    {
        Assert.Equal(2, TokenPrefix.CommonLength(new[] { 1, 2, 9, 9 }, new[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Zero_when_first_token_differs()
    {
        Assert.Equal(0, TokenPrefix.CommonLength(new[] { 7, 1 }, new[] { 1, 2 }));
    }

    [Fact]
    public void Zero_when_either_empty()
    {
        Assert.Equal(0, TokenPrefix.CommonLength(System.Array.Empty<int>(), new[] { 1, 2 }));
        Assert.Equal(0, TokenPrefix.CommonLength(new[] { 1, 2 }, System.Array.Empty<int>()));
    }
}
