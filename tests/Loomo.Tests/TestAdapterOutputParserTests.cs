using System.Linq;
using sk0ya.Loomo.CSharp.Testing;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class TestAdapterOutputParserTests
{
    [Fact]
    public void Parses_official_test_names_and_collapses_parameter_cases()
    {
        const string output = """
            Test run for C:\work\Tests.dll
            VSTest version 17.0
            The following Tests are available:
                Sample.Tests.Plain
                Sample.Tests.Theory(1)
                Sample.Tests.Theory(2)
            Total tests: 3
            """;

        var tests = TestAdapterOutputParser.Parse(output);

        Assert.Equal(new[] { "Sample.Tests.Plain", "Sample.Tests.Theory" },
            tests.Select(t => t.FullyQualifiedName));
        Assert.False(tests[0].IsParameterized);
        Assert.True(tests[1].IsParameterized);
        Assert.Equal(new[] { "Sample.Tests.Theory(1)", "Sample.Tests.Theory(2)" }, tests[1].Cases);
    }

    [Fact]
    public void Ignores_output_noise_and_empty_results()
    {
        const string output = """
            The following Tests are available:
            Warning: adapter message
            Results File: C:\temp\result.trx
            Total tests: 0
            """;

        Assert.Empty(TestAdapterOutputParser.Parse(output));
        Assert.Empty(TestAdapterOutputParser.Parse("dotnet test failed"));
    }

    [Fact]
    public void Parses_the_dotnet_cli_japanese_marker_too()
    {
        const string output = """
            次のテストを使用できます:
                Loomo.Tests.FeatureTests.GeneratedValueIsPresent
            """;

        var one = Assert.Single(TestAdapterOutputParser.Parse(output));
        Assert.Equal("Loomo.Tests.FeatureTests.GeneratedValueIsPresent", one.FullyQualifiedName);
    }
}
