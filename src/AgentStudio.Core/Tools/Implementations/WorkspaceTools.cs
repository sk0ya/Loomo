using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentStudio.Core.Abstractions;

namespace AgentStudio.Core.Tools.Implementations;

internal static class ArgHelper
{
    public static string GetString(this JsonElement args, string name, string fallback = "")
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
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
