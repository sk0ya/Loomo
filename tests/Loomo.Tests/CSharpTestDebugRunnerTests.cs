using System.IO;
using sk0ya.Loomo.CSharp.Testing;

namespace sk0ya.Loomo.Tests;

[Collection(CSharpExternalProcessCollection.Name)]
public sealed class CSharpTestDebugRunnerTests
{
    [Theory]
    [InlineData("Process Id: 52180", 52180)]
    [InlineData("プロセス ID: 42", 42)]
    [InlineData("Process Id: 0", null)]
    [InlineData("testhostを起動しました", null)]
    public void Parses_testhost_process_id(string line, int? expected)
        => Assert.Equal(expected, CSharpTestDebugProcess.ParseProcessId(line));

    [Fact]
    public async Task Resolves_test_assembly_from_msbuild_target_path()
    {
        var project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "CSharpIde",
            "tests", "Feature.Tests", "Feature.Tests.csproj"));
        if (!File.Exists(project)) return;

        var path = await CSharpTestDebugTargetResolver.ResolveAssemblyPathAsync(
            project, "net10.0", "Debug");

        Assert.NotNull(path);
        Assert.EndsWith("Loomo.CSharpFixture.Tests.dll", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starts_debug_host_and_stops_the_process_tree()
    {
        var assembly = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "CSharpIde",
            "tests", "Feature.Tests", "bin", "Debug", "net10.0", "Loomo.CSharpFixture.Tests.dll"));
        if (!File.Exists(assembly)) return;

        await using var runner = await CSharpTestDebugProcess.StartAsync(
            assembly, "FullyQualifiedName~GeneratedValueIsPresent");
        Assert.True(runner.TestHostProcessId is > 0);

        runner.Stop();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
