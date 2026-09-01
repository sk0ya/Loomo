using Loomo.CSharpFixture.Feature;

namespace Loomo.CSharpFixture.Tests;

public static class FeatureTests
{
    [Fact]
    public static void GeneratedValueIsPresent()
        => Assert.Equal("from-analyzer-config", new FeatureService().GetValue());
}
