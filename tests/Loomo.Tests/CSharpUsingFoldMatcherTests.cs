using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpUsingFoldMatcherTests
{
    [Fact]
    public void Find_matches_using_range_even_when_kind_is_null()
    {
        const string text = """
            using System;
            using System.Collections.Generic;

            namespace Sample;

            public sealed class Foo
            {
            }
            """;
        var imports = new LspFoldingRange(0, 1);
        var type = new LspFoldingRange(5, 7);

        var result = CSharpUsingFoldMatcher.Find(text, [imports, type]);

        Assert.Equal([imports], result);
    }

    [Fact]
    public void Find_does_not_match_class_or_method_ranges()
    {
        const string text = """
            using System;
            using System.Linq;

            public sealed class Foo
            {
                public void Run()
                {
                }
            }
            """;

        var result = CSharpUsingFoldMatcher.Find(text,
            [new LspFoldingRange(3, 8), new LspFoldingRange(5, 7)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Find_does_not_use_an_outer_namespace_range_that_contains_usings()
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

        var result = CSharpUsingFoldMatcher.Find(text, [new LspFoldingRange(0, 8)]);

        Assert.Empty(result);
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

        var result = CSharpUsingFoldMatcher.Find(text, [new LspFoldingRange(3, 8)]);

        Assert.Empty(result);
    }
}
