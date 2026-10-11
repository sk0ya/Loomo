using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// ワークスペースの各フォルダーから、ハーネスの構成要素を列挙する（§37.2・§37.3）。
/// <para>読むのは<b>ID と参照だけ</b>。中身の書式（Markdown／YAML）は固定しないので、<c>harness.yaml</c> も
/// <c>id:</c> と <c>version:</c> の2行を拾うだけで YAML としては解釈しない。</para>
/// </summary>
public static class HarnessDefinitionReader
{
    public const string HarnessFolderName = ".harness";

    private static readonly (string Folder, HarnessComponentKind Kind)[] HarnessComponentFolders =
    [
        ("agents", HarnessComponentKind.Agent),
        ("workflows", HarnessComponentKind.Workflow),
        ("rules", HarnessComponentKind.Rule),
        ("evaluations", HarnessComponentKind.Evaluation),
    ];

    public static HarnessDefinition Read(IEnumerable<string> folders)
    {
        var components = new List<HarnessComponent>();
        var tasks = new List<HarnessTask>();
        var harnessFolders = new List<string>();
        string? harnessId = null, version = null;

        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                continue;
            ReadClaudeCode(folder, components);

            var harness = Path.Combine(folder, HarnessFolderName);
            if (!Directory.Exists(harness))
                continue;
            harnessFolders.Add(harness);
            var (id, ver) = ReadManifest(Path.Combine(harness, "harness.yaml"));
            harnessId ??= id;
            version ??= ver;
            foreach (var (sub, kind) in HarnessComponentFolders)
            {
                foreach (var file in Files(Path.Combine(harness, sub)))
                    components.Add(new HarnessComponent(Key(kind, file), kind, Path.GetFileNameWithoutExtension(file), file));
            }
            foreach (var file in Files(Path.Combine(harness, "tasks")))
                tasks.Add(new HarnessTask(Path.GetFileNameWithoutExtension(file), ReadTitle(file), file));
        }

        return new HarnessDefinition(harnessFolders, harnessId, version, components, tasks);
    }

    /// <summary>Claude Code が読む構成要素（設定されているもの）。</summary>
    private static void ReadClaudeCode(string folder, List<HarnessComponent> components)
    {
        foreach (var name in new[] { "CLAUDE.md", "AGENTS.md", Path.Combine(".claude", "CLAUDE.md") })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                components.Add(new HarnessComponent(Key(HarnessComponentKind.Instructions, path),
                    HarnessComponentKind.Instructions, Path.GetFileName(path), path));
        }

        var claude = Path.Combine(folder, ".claude");
        if (!Directory.Exists(claude))
            return;

        var skills = Path.Combine(claude, "skills");
        if (Directory.Exists(skills))
        {
            foreach (var dir in SafeDirectories(skills))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (File.Exists(skillFile))
                    components.Add(new HarnessComponent(Key(HarnessComponentKind.Skill, skillFile),
                        HarnessComponentKind.Skill, Path.GetFileName(dir), skillFile));
            }
        }
        foreach (var file in Files(Path.Combine(claude, "agents")).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            components.Add(new HarnessComponent(Key(HarnessComponentKind.Agent, file),
                HarnessComponentKind.Agent, Path.GetFileNameWithoutExtension(file), file));
        foreach (var file in Files(Path.Combine(claude, "commands"), recursive: true)
                     .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            components.Add(new HarnessComponent(Key(HarnessComponentKind.Command, file),
                HarnessComponentKind.Command, "/" + Path.GetFileNameWithoutExtension(file), file));

        foreach (var settings in new[] { "settings.json", "settings.local.json" })
        {
            var path = Path.Combine(claude, settings);
            foreach (var hookEvent in ReadHookEvents(path))
                components.Add(new HarnessComponent($"hook:{path}#{hookEvent}",
                    HarnessComponentKind.Hook, $"{hookEvent}（{settings}）", path));
        }
    }

    /// <summary><c>settings.json</c> の <c>hooks</c> のイベント名（PreToolUse 等）。壊れていれば何も返さない。</summary>
    public static IReadOnlyList<string> ReadHookEvents(string settingsPath)
    {
        if (!File.Exists(settingsPath))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("hooks", out var hooks) && hooks.ValueKind == JsonValueKind.Object)
                return hooks.EnumerateObject().Select(p => p.Name).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return [];
    }

    /// <summary><c>harness.yaml</c> の <c>id:</c> と <c>version:</c>（行頭のキーだけ・引用符は外す）。</summary>
    public static (string? Id, string? Version) ReadManifest(string path)
    {
        if (!File.Exists(path))
            return (null, null);
        string? id = null, version = null;
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("id:", StringComparison.Ordinal))
                    id ??= Unquote(line[3..]);
                else if (line.StartsWith("version:", StringComparison.Ordinal))
                    version ??= Unquote(line[8..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (id, version);
    }

    /// <summary>タスクの題：最初の Markdown 見出し、または <c>title:</c> の行。無ければファイル名。</summary>
    public static string ReadTitle(string path)
    {
        try
        {
            foreach (var raw in File.ReadLines(path).Take(40))
            {
                var line = raw.Trim();
                if (line.StartsWith('#'))
                    return line.TrimStart('#').Trim();
                if (line.StartsWith("title:", StringComparison.Ordinal))
                    return Unquote(line[6..]) ?? Path.GetFileNameWithoutExtension(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? Unquote(string value)
    {
        var v = value.Trim().Trim('"', '\'');
        return v.Length == 0 ? null : v;
    }

    private static string Key(HarnessComponentKind kind, string path) => $"{kind.ToString().ToLowerInvariant()}:{path}";

    private static IEnumerable<string> Files(string dir, bool recursive = false)
    {
        if (!Directory.Exists(dir))
            return [];
        try
        {
            return Directory.GetFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
