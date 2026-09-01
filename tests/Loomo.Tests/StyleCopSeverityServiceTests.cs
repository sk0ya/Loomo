using System.IO;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class StyleCopSeverityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "loomo-stylecop-severity-" + Guid.NewGuid().ToString("N"));

    public StyleCopSeverityServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Creates_project_local_editorconfig_when_no_local_file_exists()
    {
        var result = new StyleCopSeverityService().SetSeverity(Project(), "sa1101", "ERROR");

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.CreatedFile);
        var text = File.ReadAllText(result.FilePath);
        Assert.Contains("[*.cs]", text);
        Assert.Contains("dotnet_diagnostic.SA1101.severity = error", text);
    }

    [Fact]
    public void Updates_existing_rule_without_touching_other_settings()
    {
        var path = Path.Combine(_root, ".editorconfig");
        File.WriteAllText(path, "root = true\n\n[*.cs]\nindent_size = 4\ndotnet_diagnostic.SA1101.severity = warning\n");

        var result = new StyleCopSeverityService().SetSeverity(Project(), "SA1101", "suggestion");

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.CreatedFile);
        var text = File.ReadAllText(path);
        Assert.Contains("indent_size = 4", text);
        Assert.Contains("dotnet_diagnostic.SA1101.severity = suggestion", text);
        Assert.DoesNotContain("dotnet_diagnostic.SA1101.severity = warning", text);
    }

    [Theory]
    [InlineData("SA11", "warning")]
    [InlineData("SA1101", "default")]
    public void Rejects_invalid_rule_or_severity(string ruleId, string severity)
    {
        var result = new StyleCopSeverityService().SetSeverity(Project(), ruleId, severity);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(Path.Combine(_root, ".editorconfig")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private ProjectModel Project()
        => new("Demo", Path.Combine(_root, "Demo.csproj"), _root, [], [], null,
            false, ProjectLoadState.Ready);
}
