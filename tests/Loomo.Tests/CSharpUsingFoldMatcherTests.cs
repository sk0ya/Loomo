using sk0ya.Loomo.CSharp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>using 節の範囲は<b>本文だけ</b>から決める（サーバーの foldingRange を待たない）。</summary>
public sealed class CSharpUsingFoldMatcherTests
{
    [Fact]
    public void Find_returns_the_leading_using_block()
    {
        const string text = """
            using System;
            using System.Collections.Generic;

            namespace Sample;

            public sealed class Foo
            {
            }
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(0, range.StartLine);
        Assert.Equal(1, range.EndLine);
    }

    /// <summary>空行やコメントで区切って書く流儀があるので、そこでは固まりを切らない。</summary>
    [Fact]
    public void Find_keeps_groups_separated_by_blank_lines_together()
    {
        const string text = """
            using System;

            // Loomo
            using sk0ya.Loomo.Core;
            using sk0ya.Loomo.Services;

            namespace Sample;
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(0, range.StartLine);
        Assert.Equal(4, range.EndLine);
    }

    /// <summary>namespace ブロックの中に書かれた using も、その固まりだけを畳む
    /// （外側の namespace ごと畳まない）。</summary>
    [Fact]
    public void Find_matches_usings_inside_a_namespace_block()
    {
        const string text = """
            namespace Sample
            {
                using System;
                using System.Linq;

                public sealed class Foo
                {
                }
            }
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(2, range.StartLine);
        Assert.Equal(3, range.EndLine);
    }

    [Fact]
    public void Find_ignores_using_statements()
    {
        const string text = """
            public sealed class Foo
            {
                public void Run()
                {
                    using (var stream = Open())
                    {
                    }
                    using var other = Open();
                }
            }
            """;

        Assert.Empty(CSharpUsingFoldMatcher.Find(text));
    }

    /// <summary>1 行しかない固まりは畳んでも得がないので返さない。</summary>
    [Fact]
    public void Find_ignores_a_single_using()
    {
        Assert.Empty(CSharpUsingFoldMatcher.Find("using System;\n\nnamespace Sample;\n"));
    }

    /// <summary>コメントアウトされた using は畳まない。ここで作る折りたたみは手動扱いなので、
    /// サーバーからの foldingRange 更新では二度と外れない＝頼んでいない折りたたみが居座る。</summary>
    [Fact]
    public void Find_ignores_usings_inside_a_block_comment()
    {
        const string text = """
            /*
            using System;
            using System.IO;
            */

            namespace Sample;
            """;

        Assert.Empty(CSharpUsingFoldMatcher.Find(text));
    }

    /// <summary>ブロックコメントは区切りではない（空行や // と同じ扱い）。</summary>
    [Fact]
    public void Find_keeps_groups_separated_by_a_block_comment_together()
    {
        const string text = """
            using System;
            /* いったん外した
            using System.IO;
            */
            using sk0ya.Loomo.Core;

            namespace Sample;
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(0, range.StartLine);
        Assert.Equal(4, range.EndLine);
    }

    /// <summary>行コメントの中の <c>/*</c> はブロックの始まりではない
    /// （始まりを誤ると、その後ろの本物の using が全部コメント扱いで消える）。</summary>
    [Fact]
    public void Find_does_not_open_a_block_comment_inside_a_line_comment()
    {
        const string text = """
            // /* ここはただの説明
            using System;
            using System.IO;

            namespace Sample;
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(1, range.StartLine);
        Assert.Equal(2, range.EndLine);
    }

    /// <summary>行末に付いたブロックコメントは using の判定を壊さない。</summary>
    [Fact]
    public void Find_keeps_usings_with_a_trailing_block_comment()
    {
        const string text = """
            using System; /* 標準 */
            using System.IO;

            namespace Sample;
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(0, range.StartLine);
        Assert.Equal(1, range.EndLine);
    }
}
