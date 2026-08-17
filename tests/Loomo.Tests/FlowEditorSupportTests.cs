using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed partial class FlowEditorSupportTests
{
    [Fact]
    public void Provider_HandlesFlowFilesAsPathBackedVisual()
    {
        var provider = new FlowEditorSupport();

        Assert.Contains(".flow", provider.SupportedExtensions);
        Assert.False(provider.UsesEditorText);
        Assert.Equal("Flow: plan.flow", provider.DescribeTitle(Path.Combine("C:\\work", "plan.flow")));
    }

    [Fact]
    public void Registry_ResolvesFlowProvider()
    {
        var provider = new FlowEditorSupport();
        var registry = new EditorSupportRegistry([provider]);

        Assert.Same(provider, registry.Resolve("C:\\work\\plan.flow"));
        Assert.Same(provider, registry.Resolve("C:\\work\\PLAN.FLOW"));
        Assert.Null(registry.Resolve("C:\\work\\plan.json"));
    }
}
