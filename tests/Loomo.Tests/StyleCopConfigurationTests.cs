using System;
using System.IO;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class StyleCopConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-stylecop-" + Guid.NewGuid().ToString("N"));

    public StyleCopConfigurationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Package_reference_and_valid_stylecop_json_are_reported_as_installed()
    {
        var config = Path.Combine(_root, "stylecop.json");
        File.WriteAllText(config, "{\"settings\":{\"documentationRules\":{}}}");
        var analyzer = typeof(StyleCopConfigurationService).Assembly.Location;
        var project = Project(analyzers: [new ProjectItem(
            "StyleCop.Analyzers.dll", analyzer)]) with
        {
            PackageReferences = ["StyleCop.Analyzers"]
        };

        var result = new StyleCopConfigurationService().Resolve(project);

        Assert.Equal(StyleCopConfigurationState.Installed, result.State);
        Assert.True(result.IsInstalled);
        Assert.Single(result.AnalyzerPaths);
        Assert.True(result.HasResolvedAnalyzer);
        Assert.Single(result.ConfigurationFiles);
        Assert.Contains("StyleCop ✓", result.StatusText);
    }

    [Fact]
    public void Package_reference_without_an_evaluated_analyzer_is_reported_as_not_loaded()
    {
        var result = new StyleCopConfigurationService().Resolve(
            Project(packageReferences: ["StyleCop.Analyzers"]));

        Assert.Equal(StyleCopConfigurationState.AnalyzerNotLoaded, result.State);
        Assert.True(result.IsInstalled);
        Assert.False(result.HasResolvedAnalyzer);
        Assert.Contains("Analyzer未読込", result.StatusText);
    }

    [Fact]
    public void Invalid_evaluated_analyzer_path_is_reported_as_not_loaded()
    {
        var analyzer = Path.Combine(_root, "StyleCop.Analyzers.dll");
        File.WriteAllBytes(analyzer, [0x4D, 0x5A]);
        var result = new StyleCopConfigurationService().Resolve(
            Project(analyzers: [new ProjectItem("StyleCop.Analyzers.dll", analyzer)]));

        Assert.Equal(StyleCopConfigurationState.AnalyzerNotLoaded, result.State);
        Assert.False(result.HasResolvedAnalyzer);
    }

    [Fact]
    public void Invalid_stylecop_json_is_distinguished_from_analyzer_violation()
    {
        File.WriteAllText(Path.Combine(_root, ".stylecop.json"), "{ invalid");
        var project = Project(packageReferences: ["StyleCop.Analyzers"]);

        var result = new StyleCopConfigurationService().Resolve(project);

        Assert.Equal(StyleCopConfigurationState.InvalidConfiguration, result.State);
        Assert.Contains(".stylecop.json", result.Error);
    }

    [Fact]
    public void Missing_package_and_analyzer_is_reported_as_not_installed()
    {
        var result = new StyleCopConfigurationService().Resolve(Project());

        Assert.Equal(StyleCopConfigurationState.NotInstalled, result.State);
        Assert.False(result.IsInstalled);
    }

    [Fact]
    public void Editorconfig_and_ruleset_severities_are_exposed_with_their_sources()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true
            [*.cs]
            dotnet_diagnostic.SA1101.severity = error
            dotnet_analyzer_diagnostic.category-StyleCop.CSharp.severity = warning
            """);
        File.WriteAllText(Path.Combine(_root, "rules.ruleset"), """
            <RuleSet Name="sample" Description="sample" ToolsVersion="15.0">
              <Rules AnalyzerId="StyleCop.Analyzers" RuleNamespace="StyleCop.CSharp">
                <Rule Id="SA1200" Action="Warning" />
              </Rules>
            </RuleSet>
            """);

        var result = new StyleCopConfigurationService().Resolve(
            Project(packageReferences: ["StyleCop.Analyzers"]));

        Assert.Equal(StyleCopConfigurationState.AnalyzerNotLoaded, result.State);
        Assert.True(result.IsInstalled);
        Assert.Single(result.EditorConfigFiles);
        Assert.Contains(result.RuleSettings, x => x.RuleId == "SA1101" && x.Severity == "error");
        Assert.Contains(result.RuleSettings, x => x.RuleId == "SA1200" && x.Severity == "Warning");
        Assert.Contains("ルール", result.StatusText);
    }

    [Fact]
    public void Central_package_management_marks_stylecop_as_installed()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="StyleCop.Analyzers" Version="1.2.0" />
              </ItemGroup>
            </Project>
            """);

        var result = new StyleCopConfigurationService().Resolve(Project());

        Assert.Equal(StyleCopConfigurationState.AnalyzerNotLoaded, result.State);
        Assert.True(result.IsInstalled);
    }

    private ProjectModel Project(
        IReadOnlyList<ProjectItem>? analyzers = null,
        IReadOnlyList<string>? packageReferences = null)
    {
        var source = Path.Combine(_root, "App.cs");
        return new ProjectModel("App", Path.Combine(_root, "App.csproj"), _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("App.cs", source)], analyzers ?? [], [], [])],
            "net10.0", false, ProjectLoadState.Ready)
        {
            PackageReferences = packageReferences ?? []
        };
    }
}
