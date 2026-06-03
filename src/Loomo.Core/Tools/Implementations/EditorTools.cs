using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>ファイルをエディタで開く。</summary>
public sealed class OpenInEditorTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    public OpenInEditorTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

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
        var resolved = _workspace.ResolvePath(path);
        await _editor.OpenFileAsync(resolved);
        return ToolResult.Ok($"開きました: {resolved}");
    }
}

/// <summary>編集案を差分提示し、承認後に適用する。</summary>
public sealed class ProposeEditTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    public ProposeEditTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

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
        var content = args.GetString("content");
        var path = _workspace.ResolvePath(args.GetString("path"));
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        var (added, removed) = DiffUtil.Stat(current, content);
        var verb = File.Exists(path) ? "編集" : "新規作成";
        var diff = DiffUtil.ToUnifiedText(DiffUtil.Compute(current, content));
        // 1行目はヘッダ（コンテキスト扱い）、2行目以降が +/-/… 接頭辞付きの差分
        return $" {verb}: {path}  (+{added} / -{removed})\n{diff}";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.GetString("path");
        var content = args.GetString("content");
        if (string.IsNullOrWhiteSpace(rawPath)) return ToolResult.Error("path は必須です。");

        var path = _workspace.ResolvePath(rawPath);
        await _editor.ShowDiffAsync(path, content);
        var ok = await _editor.ApplyEditAsync(path, content);
        return ok ? ToolResult.Ok($"適用しました: {path}") : ToolResult.Error("適用に失敗しました。");
    }
}

/// <summary>ファイル内の一意な文字列を置換する。</summary>
public sealed class ReplaceTextOnceTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;

    public ReplaceTextOnceTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

    public string Name => "replace_text_once";
    public bool RequiresApproval => true;

    public ToolDefinition Definition => new(
        Name,
        "ファイル内で old_text が一意に一致した場合だけ new_text へ置換し、差分提示後に適用・保存する。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "編集対象ファイルのパス。", true),
            ("old_text", "string", "置換前の文字列。ファイル内で一意に一致する必要がある。", true),
            ("new_text", "string", "置換後の文字列。", true),
            ("case_sensitive", "boolean", "大文字小文字を区別するか。省略時 true。", false)));

    public string DescribeInvocation(JsonElement args)
    {
        var rawPath = args.GetString("path");
        var path = _workspace.ResolvePath(rawPath);
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!TryBuildProposedContent(args, current, out var proposed, out var error))
            return $"一意文字列置換: {path}\n{error}";

        return DescribeEdit("一意文字列置換", path, current, proposed);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.GetString("path");
        if (string.IsNullOrWhiteSpace(rawPath)) return ToolResult.Error("path は必須です。");

        var path = _workspace.ResolvePath(rawPath);
        var current = await File.ReadAllTextAsync(path, ct);
        if (!TryBuildProposedContent(args, current, out var proposed, out var error))
            return ToolResult.Error(error);

        return await ApplyProposedAsync(_editor, path, proposed);
    }

    private static bool TryBuildProposedContent(
        JsonElement args,
        string current,
        out string proposed,
        out string error)
    {
        proposed = current;
        error = "";

        var oldText = args.GetString("old_text");
        var newText = args.GetString("new_text");
        if (string.IsNullOrEmpty(oldText))
        {
            error = "old_text は必須です。";
            return false;
        }

        var comparison = args.GetBool("case_sensitive", true)
            ? System.StringComparison.Ordinal
            : System.StringComparison.OrdinalIgnoreCase;
        var count = CountOccurrences(current, oldText, comparison);
        if (count == 0)
        {
            error = "old_text が見つかりません。";
            return false;
        }

        if (count > 1)
        {
            error = $"old_text が {count} 件一致しました。一意にしてください。";
            return false;
        }

        var index = current.IndexOf(oldText, comparison);
        proposed = current[..index] + newText + current[(index + oldText.Length)..];
        return true;
    }

    private static int CountOccurrences(string text, string value, System.StringComparison comparison)
    {
        var count = 0;
        var offset = 0;
        while (offset <= text.Length)
        {
            var index = text.IndexOf(value, offset, comparison);
            if (index < 0) break;
            count++;
            offset = index + value.Length;
        }

        return count;
    }

    internal static async Task<ToolResult> ApplyProposedAsync(IEditorService editor, string path, string proposed)
    {
        await editor.ShowDiffAsync(path, proposed);
        var ok = await editor.ApplyEditAsync(path, proposed);
        return ok ? ToolResult.Ok($"適用しました: {path}") : ToolResult.Error("適用に失敗しました。");
    }

    internal static string DescribeEdit(string verb, string path, string current, string proposed)
    {
        var (added, removed) = DiffUtil.Stat(current, proposed);
        var diff = DiffUtil.ToUnifiedText(DiffUtil.Compute(current, proposed));
        return $" {verb}: {path}  (+{added} / -{removed})\n{diff}";
    }
}

/// <summary>ファイルの指定行範囲を置換する。</summary>
public sealed class ReplaceRangeTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;

    public ReplaceRangeTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

    public string Name => "replace_range";
    public bool RequiresApproval => true;

    public ToolDefinition Definition => new(
        Name,
        "ファイルの 1 始まり・両端含む行範囲を replacement へ置換し、差分提示後に適用・保存する。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "編集対象ファイルのパス。", true),
            ("start_line", "integer", "置換開始行。1 始まり。", true),
            ("end_line", "integer", "置換終了行。1 始まり、両端含む。", true),
            ("replacement", "string", "置換後のテキスト。複数行可。", true)));

    public string DescribeInvocation(JsonElement args)
    {
        var path = _workspace.ResolvePath(args.GetString("path"));
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!TryBuildProposedContent(args, current, out var proposed, out var error))
            return $"行範囲置換: {path}:{args.GetInt("start_line", 0)}-{args.GetInt("end_line", 0)}\n{error}";

        return ReplaceTextOnceTool.DescribeEdit(
            $"行範囲置換 {args.GetInt("start_line", 0)}-{args.GetInt("end_line", 0)}",
            path,
            current,
            proposed);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.GetString("path");
        if (string.IsNullOrWhiteSpace(rawPath)) return ToolResult.Error("path は必須です。");

        var startLine = args.GetInt("start_line", 0);
        var endLine = args.GetInt("end_line", 0);
        if (startLine < 1) return ToolResult.Error("start_line は 1 以上で指定してください。");
        if (endLine < startLine) return ToolResult.Error("end_line は start_line 以上で指定してください。");

        var path = _workspace.ResolvePath(rawPath);
        var current = await File.ReadAllTextAsync(path, ct);
        if (!TryBuildProposedContent(args, current, out var proposed, out var error))
            return ToolResult.Error(error);

        return await ReplaceTextOnceTool.ApplyProposedAsync(_editor, path, proposed);
    }

    private static bool TryBuildProposedContent(
        JsonElement args,
        string current,
        out string proposed,
        out string error)
    {
        proposed = current;
        error = "";

        var startLine = args.GetInt("start_line", 0);
        var endLine = args.GetInt("end_line", 0);
        if (startLine < 1)
        {
            error = "start_line は 1 以上で指定してください。";
            return false;
        }

        if (endLine < startLine)
        {
            error = "end_line は start_line 以上で指定してください。";
            return false;
        }

        var replacement = args.GetString("replacement");
        if (!TryGetLineRangeOffsets(current, startLine, endLine, out var startOffset, out var endOffset, out var lineCount))
        {
            error = $"指定行が範囲外です。ファイルは {lineCount} 行です。";
            return false;
        }

        var newline = DetectNewLine(current);
        if (replacement.Length > 0
            && endOffset < current.Length
            && !replacement.EndsWith("\n", System.StringComparison.Ordinal)
            && !replacement.EndsWith("\r", System.StringComparison.Ordinal))
        {
            replacement += newline;
        }

        proposed = current[..startOffset] + replacement + current[endOffset..];
        return true;
    }

    private static bool TryGetLineRangeOffsets(
        string text,
        int startLine,
        int endLine,
        out int startOffset,
        out int endOffset,
        out int lineCount)
    {
        startOffset = -1;
        endOffset = -1;
        lineCount = 1;

        if (startLine == 1) startOffset = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            if (lineCount == endLine)
            {
                endOffset = i + 1;
                break;
            }

            lineCount++;
            if (lineCount == startLine) startOffset = i + 1;
        }

        if (endOffset < 0 && lineCount == endLine)
        {
            endOffset = text.Length;
        }

        return startOffset >= 0 && endOffset >= startOffset;
    }

    private static string DetectNewLine(string text)
        => text.Contains("\r\n", System.StringComparison.Ordinal) ? "\r\n" : "\n";
}

/// <summary>エディタの現在選択テキストを取得する。</summary>
public sealed class GetSelectionTextTool : IAgentTool
{
    private readonly IEditorService _editor;

    public GetSelectionTextTool(IEditorService editor) => _editor = editor;

    public string Name => "get_selection_text";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "エディタで現在選択されているテキストを返す。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "エディタ選択範囲取得";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var selection = await _editor.GetSelectedTextAsync();
        return ToolResult.Ok(string.IsNullOrEmpty(selection) ? "(選択なし)" : selection);
    }
}
