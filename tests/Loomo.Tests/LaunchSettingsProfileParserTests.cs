using System.IO;
using sk0ya.Loomo.CSharp.Debug;

namespace sk0ya.Loomo.Tests;

public sealed class LaunchSettingsProfileParserTests
{
    [Fact]
    public void Parses_supported_and_unsupported_profiles_without_losing_environment_values()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-launchSettings-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App", "App.csproj");
        Directory.CreateDirectory(Path.Combine(root, "App", "Properties"));
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(Path.Combine(root, "App", "Properties", "launchSettings.json"), """
            {"profiles": {
              "Project": {"commandName":"Project", "workingDirectory":"run", "commandLineArgs":"--dev \"two words\"",
                "launchBrowser":true, "applicationUrl":"https://localhost:7443;http://localhost:5080", "launchUrl":"swagger/index.html",
                "environmentVariables":{"ASPNETCORE_ENVIRONMENT":"Development","EMPTY":""}},
              "IIS Express": {"commandName":"IISExpress"},
              "Broken": "not an object"
            }}
            """);

        try
        {
            var profiles = LaunchSettingsProfileParser.ParseProject(project, out var error);

            Assert.Null(error);
            Assert.Equal(2, profiles.Count);
            var projectProfile = Assert.Single(profiles, p => p.Name == "Project");
            Assert.True(projectProfile.IsSupported);
            Assert.Equal("--dev \"two words\"", projectProfile.CommandLineArgs);
            Assert.Equal("run", projectProfile.WorkingDirectory);
            Assert.Equal("Development", projectProfile.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"]);
            Assert.True(projectProfile.LaunchBrowser);
            Assert.Equal("https://localhost:7443/swagger/index.html", projectProfile.BrowserUrl);
            Assert.False(Assert.Single(profiles, p => p.Name == "IIS Express").IsSupported);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ignores_non_http_application_urls_and_unsafe_launch_urls()
    {
        var profile = new LaunchSettingsProfile(
            "x", "Project", null, null, null, new Dictionary<string, string>(),
            LaunchBrowser: true, ApplicationUrl: "ftp://localhost:21;http://localhost:5000", LaunchUrl: "javascript:alert(1)");

        Assert.Null(profile.BrowserUrl);
    }

    [Fact]
    public void Missing_file_is_an_empty_non_error_result()
    {
        var profiles = LaunchSettingsProfileParser.ParseProject(
            Path.Combine(Path.GetTempPath(), "missing", "App.csproj"), out var error);

        Assert.Null(error);
        Assert.Empty(profiles);
    }

    [Fact]
    public void Reads_iis_express_settings_and_marks_the_profile_run_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisSettings-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App", "App.csproj");
        Directory.CreateDirectory(Path.Combine(root, "App", "Properties"));
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(Path.Combine(root, "App", "Properties", "launchSettings.json"), """
            {
              "iisSettings": {
                "iisExpress": { "applicationUrl": "http://localhost:53123", "sslPort": 44321 }
              },
              "profiles": {
                "IIS Express": { "commandName": "IISExpress", "launchBrowser": true, "launchUrl": "swagger" }
              }
            }
            """);

        try
        {
            var profile = Assert.Single(LaunchSettingsProfileParser.ParseProject(project, out var error));

            Assert.Null(error);
            Assert.True(profile.IsIisExpress);
            Assert.True(profile.IsRunSupported);
            Assert.False(profile.IsSupported);
            Assert.True(profile.IsDebugSupported);
            Assert.Equal(44321, profile.IisExpressSslPort);
            Assert.Equal("http://localhost:53123/swagger", profile.BrowserUrl);
            Assert.DoesNotContain("未対応", profile.DisplayName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
