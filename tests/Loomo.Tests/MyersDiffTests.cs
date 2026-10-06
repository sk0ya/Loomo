using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 整数列の差分（<see cref="MyersDiff"/>）の検証。行差分・行内差分の土台なので、
/// 「編集操作として正しい」「最短である」「打ち切っても正しい」を乱数入力で網羅的に確かめる。
/// </summary>
public class MyersDiffTests
{
    /// <summary>編集操作が a → b を正しく表すか（順序・添字・一致要素の値）。</summary>
    private static void AssertValidScript(int[] a, int[] b, List<Edit> edits)
    {
        var nextA = 0;
        var nextB = 0;
        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case EditKind.Equal:
                    Assert.Equal(nextA, edit.A);
                    Assert.Equal(nextB, edit.B);
                    Assert.Equal(a[edit.A], b[edit.B]);
                    nextA++;
                    nextB++;
                    break;
                case EditKind.Delete:
                    Assert.Equal(nextA, edit.A);
                    Assert.Equal(-1, edit.B);
                    nextA++;
                    break;
                case EditKind.Insert:
                    Assert.Equal(-1, edit.A);
                    Assert.Equal(nextB, edit.B);
                    nextB++;
                    break;
            }
        }
        Assert.Equal(a.Length, nextA);
        Assert.Equal(b.Length, nextB);
    }

    private static int LcsLength(int[] a, int[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        return dp[0, 0];
    }

    private static int[] RandomSeq(Random rng, int maxLength, int alphabet)
        => Enumerable.Range(0, rng.Next(maxLength + 1)).Select(_ => rng.Next(alphabet)).ToArray();

    [Theory]
    [InlineData(new int[0], new int[0])]
    [InlineData(new[] { 1, 2, 3 }, new int[0])]
    [InlineData(new int[0], new[] { 1, 2, 3 })]
    [InlineData(new[] { 1, 2, 3 }, new[] { 1, 2, 3 })]
    [InlineData(new[] { 1, 2, 3 }, new[] { 4, 5, 6 })]
    [InlineData(new[] { 1 }, new[] { 2 })]
    [InlineData(new[] { 1, 2, 3, 4 }, new[] { 1, 3, 4, 5 })]
    [InlineData(new[] { 1, 1, 1 }, new[] { 1, 1 })]
    public void Edge_cases_produce_valid_minimal_scripts(int[] a, int[] b)
    {
        var edits = MyersDiff.Diff(a, b);
        AssertValidScript(a, b, edits);
        Assert.Equal(LcsLength(a, b), edits.Count(e => e.Kind == EditKind.Equal));
    }

    [Fact]
    public void Random_inputs_are_valid_and_minimal()
    {
        var rng = new Random(12345);
        for (var trial = 0; trial < 3000; trial++)
        {
            // 小さな字母で衝突（同じ値）を多くし、経路の選び方が効く入力にする。
            var alphabet = 1 + rng.Next(6);
            var a = RandomSeq(rng, 30, alphabet);
            var b = RandomSeq(rng, 30, alphabet);
            var edits = MyersDiff.Diff(a, b);
            AssertValidScript(a, b, edits);
            Assert.Equal(LcsLength(a, b), edits.Count(e => e.Kind == EditKind.Equal));
        }
    }

    [Fact]
    public void Random_edits_of_a_long_sequence_are_minimal()
    {
        // 「元の列に少し手を入れたもの」——実際のファイル編集に近い形。
        var rng = new Random(7);
        for (var trial = 0; trial < 200; trial++)
        {
            var a = RandomSeq(rng, 200, 50);
            var b = a.ToList();
            for (var k = rng.Next(10); k >= 0; k--)
            {
                var at = rng.Next(b.Count + 1);
                switch (rng.Next(3))
                {
                    case 0: b.Insert(at, rng.Next(50)); break;
                    case 1: if (at < b.Count) b.RemoveAt(at); break;
                    default: if (at < b.Count) b[at] = rng.Next(50); break;
                }
            }
            var edits = MyersDiff.Diff(a, b.ToArray());
            AssertValidScript(a, b.ToArray(), edits);
            Assert.Equal(LcsLength(a, b.ToArray()), edits.Count(e => e.Kind == EditKind.Equal));
        }
    }

    [Fact]
    public void Capped_search_still_produces_valid_scripts()
    {
        // 打ち切り（maxCost）が効く小ささにして、近似の分割点でも編集操作として正しいことを確かめる。
        var rng = new Random(99);
        for (var trial = 0; trial < 2000; trial++)
        {
            var alphabet = 2 + rng.Next(8);
            var a = RandomSeq(rng, 60, alphabet);
            var b = RandomSeq(rng, 60, alphabet);
            var edits = MyersDiff.Diff(a, b, maxCost: 1 + rng.Next(4));
            AssertValidScript(a, b, edits);
        }
    }

    [Fact]
    public void Large_nearly_identical_inputs_are_fast()
    {
        // 旧実装（LCS の全表）では 5 万行×5 万行は全置換へ落ちていた入力。ほぼ同じ2列は線形に近い時間で済む。
        var a = Enumerable.Range(0, 50_000).ToArray();
        var b = a.ToList();
        b.Insert(10_000, -1);
        b.RemoveAt(30_000);
        b[40_000] = -2;
        var sw = Stopwatch.StartNew();
        var edits = MyersDiff.Diff(a, b.ToArray());
        sw.Stop();

        AssertValidScript(a, b.ToArray(), edits);
        Assert.Equal(4, edits.Count(e => e.Kind != EditKind.Equal));   // 挿入1・削除1・置換（削除1＋追加1）
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Completely_different_large_inputs_are_bounded_and_valid()
    {
        // 共通要素の無い大きな2列。探索の打ち切りが無いと O(N·D) で数秒〜かかる。
        var a = Enumerable.Range(0, 20_000).ToArray();
        var b = Enumerable.Range(100_000, 20_000).ToArray();
        var sw = Stopwatch.StartNew();
        var edits = MyersDiff.Diff(a, b);
        sw.Stop();

        AssertValidScript(a, b, edits);
        Assert.DoesNotContain(edits, e => e.Kind == EditKind.Equal);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Interleaved_large_inputs_with_many_differences_stay_valid()
    {
        // 違いが多く打ち切りに入る大きさ（偶数だけ・3の倍数だけ）。最短でなくても正しいこと。
        var a = Enumerable.Range(0, 30_000).Where(v => v % 2 == 0).ToArray();
        var b = Enumerable.Range(0, 30_000).Where(v => v % 3 == 0).ToArray();
        var sw = Stopwatch.StartNew();
        var edits = MyersDiff.Diff(a, b);
        sw.Stop();

        AssertValidScript(a, b, edits);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
    }
}
