namespace Loomo.CSharpFixture.Feature;

public static class ConditionalFeature
{
    public static string Target =>
#if NET10_0_OR_GREATER
        "net10";
#else
        "net9";
#endif
}
