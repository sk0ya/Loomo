using Loomo.CSharpFixture.Contracts;

namespace Loomo.CSharpFixture.Feature;

public sealed class FeatureService : IFixtureContract
{
    private readonly string _value = FixtureGenerated.Value;

    public string GetValue()
        => _value;
}
