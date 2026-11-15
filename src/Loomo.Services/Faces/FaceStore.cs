using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Faces;

namespace sk0ya.Loomo.Services.Faces;

/// <summary>読めた面、または読めなかった理由（書いた Claude がそのまま直せる英語）。</summary>
public sealed record FaceEntry(string Id, FaceManifest? Manifest, string? Error)
{
    public string Title => Manifest?.Title ?? Id;
}

/// <summary>
/// 面の置き場（§44.2）。<c>%APPDATA%/Loomo/faces/&lt;id&gt;/</c> に <c>face.json</c> と <c>index.html</c>。
/// <b>ワークスペースの中には置かない</b>——人のリポジトリに頼まれずにファイルを増やさない。
/// ディスクを読むので UI スレッドの打鍵の経路からは呼ばない（面を開く・一覧を出すときだけ）。
/// </summary>
public sealed class FaceStore
{
    public const string ManifestFileName = "face.json";
    /// <summary>面ごとの小さな置き場（<c>loomo.state</c>）。面のフォルダーの中に置き、ページには配らない。</summary>
    public const string StateFileName = ".state.json";
    public const int MaxStateBytes = 256 * 1024;

    public FaceStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "faces");
    }

    public string Root { get; }

    public string FolderOf(string id) => Path.Combine(Root, id);

    /// <summary>置いてある面を全部（名前順）。<c>face.json</c> の無いフォルダーは面ではない。</summary>
    public IReadOnlyList<FaceEntry> List()
    {
        try
        {
            if (!Directory.Exists(Root))
                return [];
            return Directory.EnumerateDirectories(Root)
                .Select(Path.GetFileName)
                .Where(name => FaceManifest.IsValidId(name) && File.Exists(Path.Combine(Root, name!, ManifestFileName)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => Find(name!))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public FaceEntry Find(string id)
    {
        if (!FaceManifest.IsValidId(id))
            return new FaceEntry(id, null, $"Invalid face id \"{id}\": use lowercase letters, digits and hyphens.");
        var path = Path.Combine(FolderOf(id), ManifestFileName);
        try
        {
            if (!File.Exists(path))
                return new FaceEntry(id, null, $"No face named \"{id}\": {path} does not exist.");
            var manifest = FaceManifest.Parse(id, File.ReadAllText(path), out var error);
            if (manifest is not null && !File.Exists(Path.Combine(FolderOf(id), "index.html")))
                return new FaceEntry(id, null, $"The face \"{id}\" has no index.html next to face.json.");
            return new FaceEntry(id, manifest, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FaceEntry(id, null, $"Could not read {path}: {ex.Message}");
        }
    }

    /// <summary>面のページが頼んだ相対パスを、面のフォルダーの中のファイルへ解く。外へ出るパス・置き場のファイル・
    /// 無いファイルは null。</summary>
    public string? ResolveFile(string id, string relativePath)
    {
        if (!FaceManifest.IsValidId(id) || relativePath.Length == 0)
            return null;
        try
        {
            var folder = Path.GetFullPath(FolderOf(id)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(Path.GetFileName(full), StateFileName, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>面の置き場の中身（JSON の文字列。無ければ <c>null</c> という JSON）。</summary>
    public string ReadState(string id)
    {
        try
        {
            var path = Path.Combine(FolderOf(id), StateFileName);
            if (!File.Exists(path))
                return "null";
            var text = File.ReadAllText(path);
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "null";
        }
    }

    public bool WriteState(string id, string json)
    {
        if (!FaceManifest.IsValidId(id) || json.Length > MaxStateBytes || !Directory.Exists(FolderOf(id)))
            return false;
        try
        {
            using var _ = JsonDocument.Parse(json);
            var path = Path.Combine(FolderOf(id), StateFileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// 面についてこの PC が覚えていること（§44.2）：<b>許した指紋</b>・開いている面・開いた回数（日別）。
/// <c>%APPDATA%/Loomo/faces-state.json</c> 1 つ。面のフォルダーの中には置かない——面を書く Claude が
/// 自分で自分を許せてしまう。読み書きは面を開く・閉じるときだけで、小さいのでその場で書く。
/// </summary>
public sealed class FaceRoomState
{
    /// <summary>これだけ開かれなかった面は「枯れた面」へ下げる（消さない。開けば戻る）。</summary>
    public const int WitherDays = 30;
    private const int KeepUsageDays = 120;

    private readonly string _path;
    private readonly object _gate = new();
    private JsonObject _root;

    public FaceRoomState(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "faces-state.json");
        _root = Load(_path);
    }

    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
                return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new JsonObject();
    }

    private JsonObject Section(string name)
    {
        if (_root[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        _root[name] = created;
        return created;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 覚えられなくても面は動く */ }
    }

    public bool IsApproved(FaceManifest manifest)
    {
        lock (_gate)
            return Section("approved")[manifest.Id] is JsonValue value && value.TryGetValue<string>(out var print)
                && print == manifest.Fingerprint;
    }

    public void Approve(FaceManifest manifest)
    {
        lock (_gate)
        {
            Section("approved")[manifest.Id] = manifest.Fingerprint;
            Save();
        }
    }

    /// <summary>開いている面（タブの並び順）と、前に出ている面。</summary>
    public (IReadOnlyList<string> Open, string? Active) OpenFaces
    {
        get
        {
            lock (_gate)
            {
                var open = (_root["open"] as JsonArray)?
                    .Select(node => node is JsonValue value && value.TryGetValue<string>(out var id) ? id : null)
                    .Where(id => FaceManifest.IsValidId(id)).Select(id => id!).Distinct().ToArray() ?? [];
                var active = _root["active"] is JsonValue a && a.TryGetValue<string>(out var text) ? text : null;
                return (open, active);
            }
        }
    }

    public void SetOpenFaces(IEnumerable<string> open, string? active)
    {
        lock (_gate)
        {
            _root["open"] = new JsonArray(open.Select(id => (JsonNode)id).ToArray());
            _root["active"] = active;
            Save();
        }
    }

    /// <summary>面を開いた（タブを新しく出した）ことを数える。</summary>
    public void RecordOpened(string id, DateTime today)
    {
        lock (_gate)
        {
            var usage = Section("usage");
            if (usage[id] is not JsonObject days)
                usage[id] = days = new JsonObject();
            var key = today.ToString("yyyy-MM-dd");
            days[key] = (days[key] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0) + 1;
            var oldest = today.AddDays(-KeepUsageDays).ToString("yyyy-MM-dd");
            foreach (var stale in days.Select(pair => pair.Key).Where(day => string.CompareOrdinal(day, oldest) < 0).ToArray())
                days.Remove(stale);
            Save();
        }
    }

    /// <summary>最後に開いた日。一度も開いていなければ null。</summary>
    public DateTime? LastOpened(string id)
    {
        lock (_gate)
        {
            if (Section("usage")[id] is not JsonObject days || days.Count == 0)
                return null;
            var last = days.Select(pair => pair.Key).Max(StringComparer.Ordinal);
            return DateTime.TryParseExact(last, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                ? day : null;
        }
    }

    /// <summary>枯れたか：最後に開いてから（開いたことが無ければ、置かれてから）<see cref="WitherDays"/> 日たった。</summary>
    public bool IsWithered(string id, DateTime createdAt, DateTime today)
        => (today.Date - (LastOpened(id) ?? createdAt).Date).TotalDays >= WitherDays;
}
