using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

internal static class ArgHelper
{
    public static string GetString(this JsonElement args, string name, string fallback = "")
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    public static int GetInt(this JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var result)
            ? result
            : fallback;

    public static bool GetBool(this JsonElement args, string name, bool fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;
}

/// <summary>フォルダ内容の一覧。</summary>
public sealed class ListDirectoryTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public ListDirectoryTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "list_directory";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "指定パス（省略時はワークスペースルート）のファイル/フォルダ一覧を返す。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "一覧するディレクトリの絶対/相対パス。省略可。", false)));

    public string DescribeInvocation(JsonElement args) => $"一覧: {args.GetString("path", "(ルート)")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) path = _workspace.RootPath ?? ".";
        var nodes = await _workspace.ListAsync(path);
        var sb = new StringBuilder();
        foreach (var n in nodes.OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name))
            sb.AppendLine(n.IsDirectory ? $"[DIR] {n.Name}" : n.Name);
        return ToolResult.Ok(sb.Length == 0 ? "(空)" : sb.ToString());
    }
}

/// <summary>ファイル読み取り。</summary>
public sealed class ReadFileTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public ReadFileTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "read_file";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ファイルの内容を読み取る。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "読み取るファイルのパス。", true)));

    public string DescribeInvocation(JsonElement args) => $"読取: {args.GetString("path")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Error("path は必須です。");
        var content = await _workspace.ReadFileAsync(path);
        return ToolResult.Ok(content);
    }
}

/// <summary>ワークスペース内のファイル名検索。</summary>
public sealed class FindFilesTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public FindFilesTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "find_files";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ワークスペース内でファイル名をワイルドカード検索する。bin/obj/.git などの生成物フォルダは除外する。",
        ToolDefinition.ObjectSchema(
            ("pattern", "string", "検索するファイル名パターン。例: *.cs, *ViewModel*, Roadmap.md", true),
            ("path", "string", "検索を開始するディレクトリの絶対/相対パス。省略時はワークスペースルート。", false),
            ("max_results", "integer", "返す最大件数。省略時100、最大500。", false)));

    public string DescribeInvocation(JsonElement args) => $"ファイル検索: {args.GetString("pattern")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var pattern = args.GetString("pattern");
        if (string.IsNullOrWhiteSpace(pattern)) return Task.FromResult(ToolResult.Error("pattern は必須です。"));

        var start = _workspace.ResolvePath(args.GetString("path"));
        if (!Directory.Exists(start)) return Task.FromResult(ToolResult.Error($"ディレクトリが存在しません: {start}"));

        var maxResults = Clamp(args.GetInt("max_results", 100), 1, 500);
        var matcher = Wildcard(pattern);
        var results = new List<string>();

        foreach (var file in EnumerateWorkspaceFiles(start, ct))
        {
            if (!matcher.IsMatch(Path.GetFileName(file))) continue;
            results.Add(ToDisplayPath(_workspace.RootPath, file));
            if (results.Count >= maxResults) break;
        }

        return Task.FromResult(ToolResult.Ok(results.Count == 0 ? "(一致なし)" : string.Join(Environment.NewLine, results)));
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private static Regex Wildcard(string pattern)
        => new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IEnumerable<string> EnumerateWorkspaceFiles(string start, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            string[] subdirs;
            string[] files;

            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subdir in subdirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!ShouldSkipDirectory(subdir)) pending.Push(subdir);
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (ShouldSkipFile(file)) continue;
                yield return file;
            }
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (IsReparsePoint(path)) return true;

        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipFile(string path) => IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static string ToDisplayPath(string? rootPath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return fullPath;
        var relative = Path.GetRelativePath(rootPath, fullPath);
        return relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? fullPath
            : relative;
    }
}

/// <summary>ワークスペース内の全文検索。</summary>
public sealed class SearchFilesTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public SearchFilesTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "search_files";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ワークスペース内のテキストファイルを全文検索し、path:line:text 形式で一致箇所を返す。",
        ToolDefinition.ObjectSchema(
            ("query", "string", "検索文字列。正規表現ではなくリテラル文字列として扱う。", true),
            ("path", "string", "検索を開始するディレクトリの絶対/相対パス。省略時はワークスペースルート。", false),
            ("file_pattern", "string", "対象ファイル名パターン。省略時は *。例: *.cs", false),
            ("case_sensitive", "boolean", "大文字小文字を区別するか。省略時 false。", false),
            ("max_results", "integer", "返す最大一致件数。省略時100、最大500。", false),
            ("max_files", "integer", "検索対象として読む最大ファイル数。省略時20000、最大100000。", false),
            ("max_file_bytes", "integer", "検索対象にする最大ファイルサイズ。省略時1048576、最大10485760。", false)));

    public string DescribeInvocation(JsonElement args) => $"全文検索: {args.GetString("query")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetString("query");
        if (string.IsNullOrEmpty(query)) return Task.FromResult(ToolResult.Error("query は必須です。"));

        var start = _workspace.ResolvePath(args.GetString("path"));
        if (!Directory.Exists(start)) return Task.FromResult(ToolResult.Error($"ディレクトリが存在しません: {start}"));

        var filePattern = args.GetString("file_pattern", "*");
        var maxResults = Math.Max(1, Math.Min(500, args.GetInt("max_results", 100)));
        var maxFiles = Math.Max(1, Math.Min(100_000, args.GetInt("max_files", 20_000)));
        var maxFileBytes = Math.Max(1, Math.Min(10 * 1024 * 1024, args.GetInt("max_file_bytes", 1024 * 1024)));
        var comparison = args.GetBool("case_sensitive", false)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var matcher = new Regex(
            "^" + Regex.Escape(filePattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var results = new List<string>();
        var inspectedFiles = 0;
        var hitFileLimit = false;

        foreach (var file in FindFilesTool.EnumerateWorkspaceFiles(start, ct))
        {
            if (!matcher.IsMatch(Path.GetFileName(file))) continue;

            inspectedFiles++;
            if (inspectedFiles > maxFiles)
            {
                hitFileLimit = true;
                break;
            }

            AddMatches(file, query, comparison, maxResults, maxFileBytes, results, ct);
            if (results.Count >= maxResults) break;
        }

        if (hitFileLimit)
        {
            results.Add($"(検索上限に達しました: max_files={maxFiles})");
        }

        return Task.FromResult(ToolResult.Ok(results.Count == 0 ? "(一致なし)" : string.Join(Environment.NewLine, results)));
    }

    private void AddMatches(
        string file,
        string query,
        StringComparison comparison,
        int maxResults,
        int maxFileBytes,
        List<string> results,
        CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > maxFileBytes) return;

            var lineNumber = 0;
            using var stream = File.OpenRead(file);
            if (ContainsNullByte(stream)) return;
            stream.Position = 0;
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                lineNumber++;
                if (!line.Contains(query, comparison)) continue;

                var displayPath = FindFilesTool.ToDisplayPath(_workspace.RootPath, file);
                results.Add($"{displayPath}:{lineNumber}:{line.Trim()}");
                if (results.Count >= maxResults) return;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (System.Text.DecoderFallbackException)
        {
        }
    }

    private static bool ContainsNullByte(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[512];
        var read = stream.Read(buffer);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0) return true;
        }

        return false;
    }
}

/// <summary>FolderTree の現在選択を取得。</summary>
public sealed class GetSelectionTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public GetSelectionTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "get_selection";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "FolderTree で現在選択中のパスと、ワークスペースルートを返す。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "選択取得";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(ToolResult.Ok(
            $"root: {_workspace.RootPath ?? "(未設定)"}\nselected: {_workspace.SelectedPath ?? "(なし)"}"));
}
