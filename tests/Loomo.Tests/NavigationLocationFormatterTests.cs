using System.IO;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class NavigationLocationFormatterTests
{
    [Fact]
    public void Shows_project_scope_and_workspace_relative_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoomoNavigation", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "src", "Feature.cs");
        var target = new TargetFrameworkModel(
            "net10.0", [], "latest",
            [new ProjectItem("src/Feature.cs", file)], [], [], []);
        var project = new ProjectModel(
            "Feature", Path.Combine(root, "Feature.csproj"), root,
            [], [target], "net10.0", false, ProjectLoadState.Ready);
        var solution = SolutionModel.NotConfigured(root) with
        {
            Projects = [project],
            State = ProjectLoadState.Ready,
        };

        var result = NavigationLocationFormatter.Resolve(file, [root], solution);

        Assert.Equal("src/Feature.cs", result.DisplayPath);
        Assert.Equal("Feature", result.Scope);
        Assert.False(result.IsExternalSource);
        Assert.Equal("src/Feature.cs:8:4 [Feature]", result.Format(7, 3));
    }

    [Fact]
    public void Labels_a_file_outside_the_workspace_as_external_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoomoNavigation", Guid.NewGuid().ToString("N"));
        var external = Path.Combine(Path.GetTempPath(), "LoomoExternal", "Generated.cs");

        var result = NavigationLocationFormatter.Resolve(external, [root], null);

        Assert.Equal(Path.GetFullPath(external), result.DisplayPath);
        Assert.Equal("外部ソース", result.Scope);
        Assert.True(result.IsExternalSource);
        Assert.Contains("[外部ソース]", result.Format(0, 0));
    }

    [Fact]
    public void Preserves_non_file_definition_uri_as_external_source()
    {
        var result = NavigationLocationFormatter.Resolve(
            "https://source.example/Library.cs", [], null);

        Assert.Equal("https://source.example/Library.cs", result.DisplayPath);
        Assert.Equal("外部ソース", result.Scope);
        Assert.True(result.IsExternalSource);
    }
}
