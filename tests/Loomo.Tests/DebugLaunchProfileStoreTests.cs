using System;
using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.Core.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ワークスペースごとのデバッグ起動プロファイル永続化（<c>%APPDATA%/Loomo/launchProfiles.json</c> 相当）の検証。</summary>
public class DebugLaunchProfileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-launch-profiles.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_of_unknown_workspace_returns_empty_and_null_selection()
    {
        var store = new DebugLaunchProfileStore(_path);
        var (profiles, selectedId) = store.Load(@"C:\some\workspace");
        Assert.Empty(profiles);
        Assert.Null(selectedId);
    }

    [Fact]
    public void Save_then_Load_round_trips_profiles_and_selection()
    {
        var store = new DebugLaunchProfileStore(_path);
        var root = @"C:\workspace-a";
        var profiles = new List<DebugLaunchProfile>
        {
            DebugLaunchProfile.CreateDefault("既定"),
            new("id-2", "サーバー", "src/Server/Server.csproj", "", true, "--port 8080", "ASPNETCORE_ENVIRONMENT=Development",
                true, false, true),
        };

        store.Save(root, profiles, "id-2");
        var (loaded, selectedId) = store.Load(root);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("既定", loaded[0].Name);
        Assert.Equal("サーバー", loaded[1].Name);
        Assert.Equal("src/Server/Server.csproj", loaded[1].ProjectPath);
        Assert.Equal("--port 8080", loaded[1].LaunchArgs);
        Assert.True(loaded[1].BreakOnUncaughtExceptions);
        Assert.Equal("id-2", selectedId);
    }

    [Fact]
    public void Save_keeps_other_workspaces_untouched()
    {
        var store = new DebugLaunchProfileStore(_path);
        var profileA = new List<DebugLaunchProfile> { DebugLaunchProfile.CreateDefault("A") };
        var profileB = new List<DebugLaunchProfile> { DebugLaunchProfile.CreateDefault("B") };

        store.Save(@"C:\workspace-a", profileA, profileA[0].Id);
        store.Save(@"C:\workspace-b", profileB, profileB[0].Id);

        var (loadedA, _) = store.Load(@"C:\workspace-a");
        var (loadedB, _) = store.Load(@"C:\workspace-b");

        Assert.Single(loadedA);
        Assert.Equal("A", loadedA[0].Name);
        Assert.Single(loadedB);
        Assert.Equal("B", loadedB[0].Name);
    }

    [Fact]
    public void Save_overwrites_previous_entry_for_same_workspace()
    {
        var store = new DebugLaunchProfileStore(_path);
        var root = @"C:\workspace-a";
        store.Save(root, new List<DebugLaunchProfile> { DebugLaunchProfile.CreateDefault("最初") }, null);
        store.Save(root, new List<DebugLaunchProfile> { DebugLaunchProfile.CreateDefault("更新後") }, null);

        var (loaded, _) = store.Load(root);
        Assert.Single(loaded);
        Assert.Equal("更新後", loaded[0].Name);
    }

    [Fact]
    public void Load_of_corrupt_file_falls_back_to_empty()
    {
        File.WriteAllText(_path, "{ not valid json");
        var store = new DebugLaunchProfileStore(_path);

        var (profiles, selectedId) = store.Load(@"C:\workspace-a");

        Assert.Empty(profiles);
        Assert.Null(selectedId);
    }
}
