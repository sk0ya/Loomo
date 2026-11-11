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

    /// <param name="userClaudeDir">ユーザー全体の Claude Code の設定フォルダー（<c>~/.claude</c>）。null なら読まない。
    /// ここの CLAUDE.md・settings.json・Skill などはどのワークスペースのセッションにも効くので、ハーネスの一部として並べる。</param>
    public static HarnessDefinition Read(IEnumerable<string> folders, string? userClaudeDir = null)
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
            // チケット駆動のワークスペース（§37.11）：.harness/ が無くても形で検出して読む。archive/ はここでは読まない。
            tasks.AddRange(HarnessLocalTickets.ReadActive(folder));
            foreach (var file in HarnessLocalTickets.WorkflowSpecFiles(folder))
                components.Add(new HarnessComponent(Key(HarnessComponentKind.Workflow, file),
                    HarnessComponentKind.Workflow, Path.GetFileNameWithoutExtension(file), file));

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
                tasks.Add(ReadTask(file));
        }

        if (!string.IsNullOrEmpty(userClaudeDir) && Directory.Exists(userClaudeDir))
            ReadClaudeFolder(userClaudeDir, components, userScope: true);

        return new HarnessDefinition(harnessFolders, harnessId, version, components, tasks);
    }

    /// <summary>Claude Code が読む構成要素（設定されているもの）。ワークスペースのフォルダー側。</summary>
    private static void ReadClaudeCode(string folder, List<HarnessComponent> components)
    {
        foreach (var name in new[] { "CLAUDE.md", "AGENTS.md" })
            AddIfExists(components, HarnessComponentKind.Instructions, Path.Combine(folder, name), userScope: false);
        // プロジェクトの MCP サーバー（.mcp.json）。ツールの数＝毎回の入力に効くので設定として並べる。
        AddIfExists(components, HarnessComponentKind.Settings, Path.Combine(folder, ".mcp.json"), userScope: false);

        var claude = Path.Combine(folder, ".claude");
        if (Directory.Exists(claude))
            ReadClaudeFolder(claude, components, userScope: false);
    }

    /// <summary>
    /// <c>.claude</c> フォルダー1つ（プロジェクトの <c>&lt;folder&gt;/.claude</c> とユーザーの <c>~/.claude</c> は同じ形）。
    /// 設定ファイルは hooks の有無によらず並べる——開きたいのは hooks だけではない（権限・モデル・環境変数）。
    /// </summary>
    private static void ReadClaudeFolder(string claude, List<HarnessComponent> components, bool userScope)
    {
        AddIfExists(components, HarnessComponentKind.Instructions, Path.Combine(claude, "CLAUDE.md"), userScope);
        foreach (var settings in new[] { "settings.json", "settings.local.json" })
            AddIfExists(components, HarnessComponentKind.Settings, Path.Combine(claude, settings), userScope);

        var skills = Path.Combine(claude, "skills");
        if (Directory.Exists(skills))
        {
            foreach (var dir in SafeDirectories(skills))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (File.Exists(skillFile))
                    components.Add(new HarnessComponent(Key(HarnessComponentKind.Skill, skillFile),
                        HarnessComponentKind.Skill, Path.GetFileName(dir), skillFile, userScope));
            }
        }
        foreach (var file in Files(Path.Combine(claude, "agents")).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            components.Add(new HarnessComponent(Key(HarnessComponentKind.Agent, file),
                HarnessComponentKind.Agent, Path.GetFileNameWithoutExtension(file), file, userScope));
        foreach (var file in Files(Path.Combine(claude, "commands"), recursive: true)
                     .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            components.Add(new HarnessComponent(Key(HarnessComponentKind.Command, file),
                HarnessComponentKind.Command, "/" + Path.GetFileNameWithoutExtension(file), file, userScope));

        foreach (var settings in new[] { "settings.json", "settings.local.json" })
        {
            var path = Path.Combine(claude, settings);
            foreach (var hookEvent in ReadHookEvents(path))
                components.Add(new HarnessComponent($"hook:{path}#{hookEvent}",
                    HarnessComponentKind.Hook, $"{hookEvent}（{settings}）", path, userScope));
        }
    }

    private static void AddIfExists(List<HarnessComponent> components, HarnessComponentKind kind, string path, bool userScope)
    {
        if (File.Exists(path))
            components.Add(new HarnessComponent(Key(kind, path), kind, Path.GetFileName(path), path, userScope,
                kind == HarnessComponentKind.Instructions ? CountLines(path) : null));
    }

    /// <summary>
    /// Claude Code の自動メモリ（<c>~/.claude/projects/&lt;slug&gt;/memory/MEMORY.md</c>）を指示として足す。
    /// 置き場所は <c>~/.claude</c> の下だが、効くのはその記録フォルダーのワークスペースだけで、毎回読み込まれる。
    /// </summary>
    public static HarnessDefinition WithAutoMemory(HarnessDefinition definition, IEnumerable<string> transcriptDirectories)
    {
        var added = new List<HarnessComponent>();
        foreach (var dir in transcriptDirectories)
        {
            var path = Path.Combine(dir, "memory", "MEMORY.md");
            if (File.Exists(path) && !definition.Components.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
                added.Add(new HarnessComponent(Key(HarnessComponentKind.Instructions, path), HarnessComponentKind.Instructions,
                    "MEMORY.md（自動メモリ）", path, UserScope: false, CountLines(path)));
        }
        return added.Count == 0 ? definition : definition with { Components = [.. definition.Components, .. added] };
    }

    private static int? CountLines(string path)
    {
        try { return File.ReadLines(path).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
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

    /// <summary>タスクファイル1つ（ID・題・<c>ticket:</c>）。</summary>
    public static HarnessTask ReadTask(string path)
        => new(Path.GetFileNameWithoutExtension(path), ReadTitle(path), path, ReadTicket(path));

    /// <summary>
    /// タスクが対応するチケット：先頭近くの <c>ticket:</c> の行（<c>AB#1234</c>・URL・番号だけ）。
    /// 指示にそのチケットが書かれた Run は、このタスクに結び付く（§37.3.2）。無ければ null。
    /// </summary>
    public static HarnessTicketRef? ReadTicket(string path)
    {
        try
        {
            var inComment = false;
            foreach (var raw in File.ReadLines(path).Take(40))
            {
                var line = raw.Trim().TrimStart('-', '*').Trim();
                // コメントの中の説明（雛形の「ticket: に AB#1234 を書くと…」）を宣言と読まない。
                if (inComment || line.StartsWith("<!--", StringComparison.Ordinal))
                {
                    inComment = !raw.Contains("-->", StringComparison.Ordinal);
                    continue;
                }
                if (line.StartsWith("ticket:", StringComparison.OrdinalIgnoreCase))
                    return HarnessTickets.ParseDeclared(line[7..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
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
