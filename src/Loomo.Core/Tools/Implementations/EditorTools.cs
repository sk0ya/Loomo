using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>ファイルをエディタで開く。</summary>
public sealed class OpenInEditorTool : IAgentTool
{
    private readonly IEditorService _editor;
    public OpenInEditorTool(IEditorService editor) => _editor = editor;

    public string Name => "open_in_editor";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "指定ファイルをエディタで開いて表示する。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "開くファイルのパス。", true)));

    public string DescribeInvocation(JsonElement args) => $"エディタで開く: {args.GetString("path")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Error("path は必須です。");
        await _editor.OpenFileAsync(path);
        return ToolResult.Ok($"開きました: {path}");
    }
}

/// <summary>編集案を差分提示し、承認後に適用する。</summary>
public sealed class ProposeEditTool : IAgentTool
{
    private readonly IEditorService _editor;
    public ProposeEditTool(IEditorService editor) => _editor = editor;

    public string Name => "propose_edit";
    public bool RequiresApproval => true;   // 書込なので承認必須

    public ToolDefinition Definition => new(
        Name,
        "ファイルの新しい全文を提示し、承認されたら適用・保存する。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "編集対象ファイルのパス。", true),
            ("content", "string", "ファイルの新しい全文。", true)));

    public string DescribeInvocation(JsonElement args)
    {
        var path = args.GetString("path");
        var len = args.GetString("content").Length;
        return $"編集適用: {path}（{len} 文字）";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString("path");
        var content = args.GetString("content");
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Error("path は必須です。");

        await _editor.ShowDiffAsync(path, content);
        var ok = await _editor.ApplyEditAsync(path, content);
        return ok ? ToolResult.Ok($"適用しました: {path}") : ToolResult.Error("適用に失敗しました。");
    }
}
