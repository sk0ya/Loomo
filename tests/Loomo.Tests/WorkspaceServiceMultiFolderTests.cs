using System;
using System.Linq;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class WorkspaceServiceMultiFolderTests
{
    private const string Primary = @"C:\ws\primary";
    private const string Secondary = @"C:\ws\secondary";

    [Fact]
    public void OpenFolder_sets_single_primary_folder()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);

        Assert.Equal(new[] { Primary }, service.Folders);
        Assert.Equal(Primary, service.RootPath);
    }

    [Fact]
    public void AddFolder_appends_and_keeps_primary_unchanged()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);

        Assert.Equal(new[] { Primary, Secondary }, service.Folders);
        Assert.Equal(Primary, service.RootPath);
    }

    [Fact]
    public void AddFolder_ignores_duplicate()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);
        service.AddFolder(Secondary);

        Assert.Equal(2, service.Folders.Count);
    }

    [Fact]
    public void AddFolder_ignores_descendant_of_existing_folder()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);
        service.AddFolder(System.IO.Path.Combine(Primary, "sub"));

        Assert.Single(service.Folders);
    }

    [Fact]
    public void AddFolder_ignores_ancestor_of_existing_folder()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(System.IO.Path.Combine(Primary, "sub"));
        service.AddFolder(Primary);

        Assert.Single(service.Folders);
    }

    [Fact]
    public void RemoveFolder_removes_non_primary_folder()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);
        service.RemoveFolder(Secondary);

        Assert.Equal(new[] { Primary }, service.Folders);
    }

    [Fact]
    public void RemoveFolder_does_not_remove_primary()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);
        service.RemoveFolder(Primary);

        Assert.Equal(new[] { Primary, Secondary }, service.Folders);
    }

    [Fact]
    public void FoldersChanged_fires_on_AddFolder_and_RemoveFolder()
    {
        var service = new WorkspaceService(new SafetySettings());
        service.OpenFolder(Primary);
        var fireCount = 0;
        service.FoldersChanged += (_, _) => fireCount++;

        service.AddFolder(Secondary);
        service.RemoveFolder(Secondary);

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void ResolvePath_allows_absolute_path_under_added_secondary_folder()
    {
        var service = new WorkspaceService(new SafetySettings { RestrictToWorkspaceRoot = true });
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);

        var target = System.IO.Path.Combine(Secondary, "file.txt");
        var resolved = service.ResolvePath(target);

        Assert.Equal(System.IO.Path.GetFullPath(target), resolved);
    }

    [Fact]
    public void ResolvePath_throws_for_path_outside_all_folders()
    {
        var service = new WorkspaceService(new SafetySettings { RestrictToWorkspaceRoot = true });
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);

        Assert.Throws<UnauthorizedAccessException>(
            () => service.ResolvePath(@"C:\elsewhere\file.txt"));
    }

    [Fact]
    public void ResolvePath_relative_path_still_resolves_against_primary_only()
    {
        var service = new WorkspaceService(new SafetySettings { RestrictToWorkspaceRoot = true });
        service.OpenFolder(Primary);
        service.AddFolder(Secondary);

        var resolved = service.ResolvePath("file.txt");

        Assert.Equal(System.IO.Path.GetFullPath(System.IO.Path.Combine(Primary, "file.txt")), resolved);
    }
}
