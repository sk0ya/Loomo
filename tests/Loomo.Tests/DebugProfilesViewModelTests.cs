using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Tests;

public sealed class DebugProfilesViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LoomoProfiles-" + Guid.NewGuid().ToString("N"));
    private readonly string _storePath;

    public DebugProfilesViewModelTests()
    {
        _storePath = Path.Combine(_root, "profiles.json");
        Directory.CreateDirectory(_root);
        CreateProject("A", "A");
        CreateProject("B", "B");
    }

    [Fact]
    public void Switching_profile_reloads_the_new_projects_launch_settings()
    {
        var workspace = new FakeWorkspaceService(_root);
        var aPath = Path.Combine(_root, "A", "A.csproj");
        var bPath = Path.Combine(_root, "B", "B.csproj");
        var store = new DebugLaunchProfileStore(_storePath);
        var a = new DebugLaunchProfile("a", "A構成", "A/A.csproj", "", true, "", "", false, false, false,
            LaunchSettingsProfileName: "A");
        var b = new DebugLaunchProfile("b", "B構成", "B/B.csproj", "", true, "", "", false, false, false,
            LaunchSettingsProfileName: "B");
        store.Save(_root, [a, b], "a");

        using var profiles = new DebugProfilesViewModel(
            workspace,
            store,
            _ =>
            [
                new DebugProjectDiscovery.ProjectEntry("A", aPath, "A/A.csproj", false),
                new DebugProjectDiscovery.ProjectEntry("B", bPath, "B/B.csproj", false),
            ]);
        var launch = new LaunchConfigurationStub();
        profiles.AttachLaunch(launch);

        Assert.Equal("A", profiles.SelectedLaunchSettingsProfile?.Name);
        Assert.Contains(profiles.LaunchSettingsProfiles, profile => profile.Name == "A");

        profiles.SelectedProfile = profiles.Profiles.Single(profile => profile.Model.Id == "b");

        Assert.Equal("B", profiles.SelectedLaunchSettingsProfile?.Name);
        Assert.Equal("https://localhost:7002/", launch.LaunchBrowserUrl);
        Assert.DoesNotContain(profiles.LaunchSettingsProfiles, profile => profile.Name == "A");
    }

    [Fact]
    public void Returns_the_selected_supported_launch_profile_only_for_its_project()
    {
        var workspace = new FakeWorkspaceService(_root);
        var aPath = Path.Combine(_root, "A", "A.csproj");
        var store = new DebugLaunchProfileStore(_storePath);
        using var profiles = new DebugProfilesViewModel(
            workspace,
            store,
            _ => [new DebugProjectDiscovery.ProjectEntry("A", aPath, "A/A.csproj", false)]);
        profiles.SelectedProject = profiles.AvailableProjects.Single(project =>
            string.Equals(project.FullPath, aPath, StringComparison.OrdinalIgnoreCase));
        profiles.SelectedLaunchSettingsProfile = profiles.LaunchSettingsProfiles.Single();

        Assert.Equal("A", profiles.SelectedSupportedLaunchProfileNameFor(aPath));
        Assert.Null(profiles.SelectedSupportedLaunchProfileNameFor(
            Path.Combine(_root, "B", "B.csproj")));
    }

    [Fact]
    public void Exposes_iis_express_for_run_and_debug_attach()
    {
        var aPath = Path.Combine(_root, "A", "A.csproj");
        File.WriteAllText(Path.Combine(_root, "A", "Properties", "launchSettings.json"), """
            {
              "iisSettings": { "iisExpress": { "applicationUrl": "http://localhost:53123", "sslPort": 44321 } },
              "profiles": {
                "IIS Express": { "commandName": "IISExpress", "launchBrowser": true, "launchUrl": "swagger" }
              }
            }
            """);
        var workspace = new FakeWorkspaceService(_root);
        var store = new DebugLaunchProfileStore(_storePath);
        using var profiles = new DebugProfilesViewModel(
            workspace,
            store,
            _ => [new DebugProjectDiscovery.ProjectEntry("A", aPath, "A/A.csproj", false)]);
        var launch = new LaunchConfigurationStub();
        profiles.AttachLaunch(launch);
        profiles.SelectedProject = profiles.AvailableProjects.Single(project =>
            string.Equals(project.FullPath, aPath, StringComparison.OrdinalIgnoreCase));
        profiles.SelectedLaunchSettingsProfile = profiles.LaunchSettingsProfiles.Single();

        Assert.True(profiles.SelectedLaunchSettingsProfile.IsIisExpress);
        Assert.Null(profiles.SelectedSupportedLaunchProfileNameFor(aPath));
        Assert.Equal("IIS Express", profiles.SelectedRunLaunchProfileFor(aPath)?.Name);
        Assert.Contains("IIS Express", profiles.LaunchSettingsStatus);
        Assert.Equal("", launch.TargetProgram);
        Assert.True(launch.BuildFirst);
    }

    private void CreateProject(string name, string profileName)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(directory, "Properties"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), "<Project />");
        var port = name == "A" ? "7001" : "7002";
        File.WriteAllText(Path.Combine(directory, "Properties", "launchSettings.json"),
            $"{{\"profiles\":{{\"{profileName}\":{{\"commandName\":\"Project\",\"launchBrowser\":true,\"applicationUrl\":\"https://localhost:{port}\"}}}}}}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LaunchConfigurationStub : ObservableObject, ILaunchConfigurationOwner, ILaunchBrowserTarget
    {
        private string _targetProgram = "";
        private bool _buildFirst;
        private string _launchArgs = "";
        private string _launchEnv = "";
        private string _workingDirectory = "";
        private bool _justMyCode;
        private bool _breakOnAllExceptions;
        private bool _breakOnUncaughtExceptions;

        public string TargetProgram { get => _targetProgram; set => SetProperty(ref _targetProgram, value); }
        public bool BuildFirst { get => _buildFirst; set => SetProperty(ref _buildFirst, value); }
        public string LaunchArgs { get => _launchArgs; set => SetProperty(ref _launchArgs, value); }
        public string LaunchEnv { get => _launchEnv; set => SetProperty(ref _launchEnv, value); }
        public string LaunchWorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
        public bool JustMyCode { get => _justMyCode; set => SetProperty(ref _justMyCode, value); }
        public bool BreakOnAllExceptions { get => _breakOnAllExceptions; set => SetProperty(ref _breakOnAllExceptions, value); }
        public bool BreakOnUncaughtExceptions { get => _breakOnUncaughtExceptions; set => SetProperty(ref _breakOnUncaughtExceptions, value); }
        public string LaunchBrowserUrl { get; set; } = "";
    }
}
