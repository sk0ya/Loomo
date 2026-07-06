using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>
/// ワークスペースごとのデバッグ起動プロファイル一式を <c>%APPDATA%/Loomo/launchProfiles.json</c> に永続化する
/// （Singleton 想定、UI 非依存）。<see cref="ConversationStore"/>/<c>WorkspaceStateStore</c> と同じ形：
/// 既定パス＋テスト用パス指定コンストラクタ、素の <see cref="JsonSerializer"/>、壊れたファイルは黙って空扱い。
/// </summary>
public sealed class DebugLaunchProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public DebugLaunchProfileStore() : this(DefaultPath()) { }

    public DebugLaunchProfileStore(string filePath) => _filePath = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "launchProfiles.json");

    /// <summary>指定ワークスペースの保存済みプロファイル一覧と選択中IDを返す。エントリが無ければ空一覧＋null。</summary>
    public (IReadOnlyList<DebugLaunchProfile> Profiles, string? SelectedId) Load(string rootPath)
    {
        var entry = LoadFile().Workspaces.FirstOrDefault(w => PathsEqual(w.RootPath, rootPath));
        return entry is null
            ? (Array.Empty<DebugLaunchProfile>(), null)
            : (entry.Profiles, entry.SelectedProfileId);
    }

    /// <summary>指定ワークスペースのプロファイル一覧と選択中IDを保存する（他ワークスペースのエントリは温存）。</summary>
    public void Save(string rootPath, IReadOnlyList<DebugLaunchProfile> profiles, string? selectedId)
    {
        var file = LoadFile();
        file.Workspaces.RemoveAll(w => PathsEqual(w.RootPath, rootPath));
        file.Workspaces.Add(new WorkspaceEntry(rootPath, profiles.ToList(), selectedId));

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(file, JsonOpts));
    }

    private FileModel LoadFile()
    {
        if (!File.Exists(_filePath)) return new FileModel(new List<WorkspaceEntry>());
        try
        {
            return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_filePath), JsonOpts)
                ?? new FileModel(new List<WorkspaceEntry>());
        }
        catch
        {
            return new FileModel(new List<WorkspaceEntry>());
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed record FileModel(List<WorkspaceEntry> Workspaces);

    private sealed record WorkspaceEntry(string RootPath, List<DebugLaunchProfile> Profiles, string? SelectedProfileId);
}
