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
    public List<TerminalTabSnapshot> TerminalTabs { get; set; } = new();
    public List<EditorTabSnapshot> EditorTabs { get; set; } = new();
    public List<BrowserTabSnapshot> BrowserTabs { get; set; } = new();

    /// <summary>
    /// メイン領域のレイアウトツリー（リーフ＝ペイン、スプリット＝行/列の入れ子）。
    /// null なら既定レイアウトを使う。非表示のペインもツリーに残り、リーフの Hidden で表す。
    /// </summary>
    public PaneNodeSnapshot? PaneLayout { get; set; }
}

/// <summary>メイン領域に並ぶペインの種別。値は JSON へ数値で永続化されるため末尾追加のみ可。</summary>
public enum PaneKind
{
    Terminal,
    Editor,
    Browser,
    Ai,
    EditorSupport,
    Git
}

/// <summary>
/// レイアウトツリーの1ノード。<see cref="Kind"/> があればリーフ（ペイン）、
/// <see cref="Children"/> があればスプリット（入れ子の行/列）。
/// </summary>
public sealed class PaneNodeSnapshot
{
    /// <summary>親スプリット内での star 比率。</summary>
    public double Weight { get; set; } = 1;
    /// <summary>リーフのとき、ペイン種別。</summary>
    public PaneKind? Kind { get; set; }
    /// <summary>リーフのとき、非表示中か。位置・比率を保ったまま隠す。</summary>
    public bool Hidden { get; set; }
    /// <summary>スプリットのとき、"Rows"（上下に積む）か "Columns"（左右に並べる）。</summary>
    public string? Orientation { get; set; }
    /// <summary>スプリットの子（行なら上→下、列なら左→右の順）。</summary>
    public List<PaneNodeSnapshot> Children { get; set; } = new();
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

public sealed class TerminalTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? WorkingDirectory { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; }
}

public sealed class EditorTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? FilePath { get; set; }
    public string? Text { get; set; }
    public string? Title { get; set; }
    public bool IsModified { get; set; }
    public bool IsActive { get; set; }
}

public sealed class BrowserTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; }
}
