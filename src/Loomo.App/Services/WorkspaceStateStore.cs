using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

public sealed class WorkspaceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public WorkspaceStateStore() : this(DefaultPath()) { }

    public WorkspaceStateStore(string filePath) => _filePath = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "workspaces.json");

    public WorkspaceState Load()
    {
        if (!File.Exists(_filePath))
            return new WorkspaceState();

        try
        {
            return JsonSerializer.Deserialize<WorkspaceState>(
                File.ReadAllText(_filePath), JsonOptions) ?? new WorkspaceState();
        }
        catch
        {
            return new WorkspaceState();
        }
    }

    public void Save(WorkspaceState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(state, JsonOptions));
    }
}

public sealed class WorkspaceState
{
    public Guid? ActiveWorkspaceId { get; set; }
    public List<WorkspaceSnapshot> Workspaces { get; set; } = new();
}

public sealed class WorkspaceSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RootPath { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public TerminalSnapshot Terminal { get; set; } = new();
    public EditorSnapshot Editor { get; set; } = new();
    public List<BrowserTabSnapshot> BrowserTabs { get; set; } = new();
}

public sealed class TerminalSnapshot
{
    public string? WorkingDirectory { get; set; }
    public string? Title { get; set; }
}

public sealed class EditorSnapshot
{
    public string? FilePath { get; set; }
    public string? Text { get; set; }
    public bool IsModified { get; set; }
}

public sealed class BrowserTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; }
}
