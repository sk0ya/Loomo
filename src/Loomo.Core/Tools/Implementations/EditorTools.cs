using System.Collections.Generic;
using System.IO;
using System.Text;
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

/// <summary>新規ファイルを作成し、全文を書き込む（既存ファイルには使わない）。</summary>
public sealed class CreateFileTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    public CreateFileTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

    public string Name => "create_file";
    public bool RequiresApproval => true;   // 書込なので承認必須

    public ToolDefinition Definition => new(
        Name,
        "新しいファイルを作成して全文を書き込み、承認されたら保存する。"
        + "既存ファイルの編集には使わない（その場合は replace_text_once か apply_patch を使う）。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "作成するファイルのパス。", true),
            ("content", "string", "ファイルの全文。", true)));

    public string DescribeInvocation(JsonElement args)
    {
        var content = args.GetString("content");
        var path = _workspace.ResolvePath(args.GetString("path"));
        if (File.Exists(path))
            return $"新規作成: {path}\n既存ファイルです。replace_text_once か apply_patch を使ってください。";

        var (added, _) = DiffUtil.Stat("", content);
        var diff = DiffUtil.ToUnifiedText(DiffUtil.Compute("", content));
        // 1行目はヘッダ（コンテキスト扱い）、2行目以降が +/-/… 接頭辞付きの差分
        return $" 新規作成: {path}  (+{added} / -0)\n{diff}";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.GetString("path");
        var content = args.GetString("content");
        if (string.IsNullOrWhiteSpace(rawPath)) return ToolResult.Error("path は必須です。");

        var path = _workspace.ResolvePath(rawPath);
        if (File.Exists(path))
            return ToolResult.Error("既存ファイルです。replace_text_once か apply_patch を使ってください。");

        await _editor.ShowDiffAsync(path, content);
        var ok = await _editor.ApplyEditAsync(path, content);
        return ok ? ToolResult.Ok($"作成しました: {path}") : ToolResult.Error("作成に失敗しました。");
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

/// <summary>1ファイルへ複数の SEARCH/REPLACE ブロックをまとめて局所適用する。</summary>
public sealed class ApplyPatchTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;

    public ApplyPatchTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

    public string Name => "apply_patch";
    public bool RequiresApproval => true;

    public ToolDefinition Definition => new(
        Name,
        "1ファイルへ複数の編集をまとめて局所適用する。patch は SEARCH/REPLACE ブロックの並びで、各ブロックは "
        + "次の形式: 行頭 '<<<<<<< SEARCH'、置換前テキスト、行頭 '======='、置換後テキスト、行頭 '>>>>>>> REPLACE'。"
        + "各 SEARCH は適用時点のファイル内で一意に一致する必要がある。ブロックは記載順に適用され、差分提示後に承認されたら保存する。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "編集対象ファイルのパス。", true),
            ("patch", "string", "SEARCH/REPLACE ブロックの並び。", true)));

    public string DescribeInvocation(JsonElement args)
    {
        var path = _workspace.ResolvePath(args.GetString("path"));
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!TryBuildProposedContent(args, current, out var proposed, out var error))
            return $"パッチ適用: {path}\n{error}";

        return ReplaceTextOnceTool.DescribeEdit("パッチ適用", path, current, proposed);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = args.GetString("path");
        if (string.IsNullOrWhiteSpace(rawPath)) return ToolResult.Error("path は必須です。");

        var path = _workspace.ResolvePath(rawPath);
        if (!File.Exists(path)) return ToolResult.Error($"ファイルが存在しません: {path}");

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

        if (!TryParseBlocks(args.GetString("patch"), out var blocks, out error))
            return false;

        var newline = current.Contains("\r\n", System.StringComparison.Ordinal) ? "\r\n" : "\n";
        var working = current;
        for (var i = 0; i < blocks.Count; i++)
        {
            // LLM は \n で書きがちなのでファイルの改行コードへ合わせて照合する
            var search = NormalizeNewLines(blocks[i].Search, newline);
            var replace = NormalizeNewLines(blocks[i].Replace, newline);
            if (search.Length == 0)
            {
                error = $"ブロック {i + 1}: SEARCH が空です。";
                return false;
            }

            var count = CountOccurrences(working, search);
            if (count == 0)
            {
                error = $"ブロック {i + 1}: SEARCH が見つかりません。";
                return false;
            }

            if (count > 1)
            {
                error = $"ブロック {i + 1}: SEARCH が {count} 件一致しました。一意にしてください。";
                return false;
            }

            var index = working.IndexOf(search, System.StringComparison.Ordinal);
            working = working[..index] + replace + working[(index + search.Length)..];
        }

        proposed = working;
        return true;
    }

    internal readonly record struct PatchBlock(string Search, string Replace);

    internal static bool TryParseBlocks(string patch, out List<PatchBlock> blocks, out string error)
    {
        blocks = new List<PatchBlock>();
        error = "";

        if (string.IsNullOrWhiteSpace(patch))
        {
            error = "patch は必須です。";
            return false;
        }

        var lines = patch.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var state = 0; // 0:外側 1:SEARCH収集 2:REPLACE収集
        var search = new StringBuilder();
        var replace = new StringBuilder();

        foreach (var line in lines)
        {
            if (state == 0)
            {
                if (line.StartsWith("<<<<<<< SEARCH", System.StringComparison.Ordinal))
                {
                    state = 1;
                    search.Clear();
                    replace.Clear();
                }
                // ブロック外の行（説明など）は無視する
            }
            else if (state == 1)
            {
                if (line == "=======")
                    state = 2;
                else
                    search.Append(line).Append('\n');
            }
            else // state == 2
            {
                if (line.StartsWith(">>>>>>> REPLACE", System.StringComparison.Ordinal))
                {
                    blocks.Add(new PatchBlock(TrimTrailingNewline(search.ToString()), TrimTrailingNewline(replace.ToString())));
                    state = 0;
                }
                else
                {
                    replace.Append(line).Append('\n');
                }
            }
        }

        if (state != 0)
        {
            error = "patch の SEARCH/REPLACE ブロックが閉じていません。'<<<<<<< SEARCH' / '=======' / '>>>>>>> REPLACE' の対応を確認してください。";
            return false;
        }

        if (blocks.Count == 0)
        {
            error = "patch に SEARCH/REPLACE ブロックがありません。";
            return false;
        }

        return true;
    }

    private static string TrimTrailingNewline(string text)
        => text.EndsWith('\n') ? text[..^1] : text;

    private static string NormalizeNewLines(string text, string newline)
    {
        var lf = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return newline == "\n" ? lf : lf.Replace("\n", newline);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while (offset <= text.Length)
        {
            var index = text.IndexOf(value, offset, System.StringComparison.Ordinal);
            if (index < 0) break;
            count++;
            offset = index + value.Length;
        }

        return count;
    }
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

/// <summary>エディタの現在選択範囲を new_text へ置換する編集案を提示・適用する。</summary>
public sealed class ReplaceSelectionTool : IAgentTool
{
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;

    public ReplaceSelectionTool(IEditorService editor, IWorkspaceService workspace)
    {
        _editor = editor;
        _workspace = workspace;
    }

    public string Name => "replace_selection";
    public bool RequiresApproval => true;

    public ToolDefinition Definition => new(
        Name,
        "エディタで現在選択されているテキストを new_text へ置換し、差分提示後に適用・保存する。"
        + "アクティブなファイルが対象で、選択テキストがファイル内で一意に一致する場合だけ適用する。",
        ToolDefinition.ObjectSchema(
            ("new_text", "string", "選択範囲を置き換える新しいテキスト。", true)));

    public string DescribeInvocation(JsonElement args)
    {
        if (!TryResolveTarget(out var path, out var current, out var selection, out var targetError))
            return $"選択範囲置換\n{targetError}";

        if (!TryBuildProposedContent(current, selection, args.GetString("new_text"), out var proposed, out var error))
            return $"選択範囲置換: {path}\n{error}";

        return ReplaceTextOnceTool.DescribeEdit("選択範囲置換", path, current, proposed);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryResolveTarget(out var path, out var current, out var selection, out var targetError))
            return ToolResult.Error(targetError);

        if (!TryBuildProposedContent(current, selection, args.GetString("new_text"), out var proposed, out var error))
            return ToolResult.Error(error);

        return await ReplaceTextOnceTool.ApplyProposedAsync(_editor, path, proposed);
    }

    /// <summary>アクティブファイルのパス・現在内容・選択テキストを取得する。
    /// IEditorService の取得系は同期完了するため Describe からも安全に待てる。</summary>
    private bool TryResolveTarget(out string path, out string current, out string selection, out string error)
    {
        path = "";
        current = "";
        selection = "";
        error = "";

        var active = _editor.ActiveFilePath;
        if (string.IsNullOrWhiteSpace(active))
        {
            error = "アクティブなファイルがありません。";
            return false;
        }

        selection = _editor.GetSelectedTextAsync().GetAwaiter().GetResult() ?? "";
        if (selection.Length == 0)
        {
            error = "エディタで選択範囲がありません。";
            return false;
        }

        path = _workspace.ResolvePath(active);
        current = _editor.GetActiveContentAsync().GetAwaiter().GetResult() ?? "";
        return true;
    }

    private static bool TryBuildProposedContent(
        string current,
        string selection,
        string newText,
        out string proposed,
        out string error)
    {
        proposed = current;
        error = "";

        var count = 0;
        var offset = 0;
        var firstIndex = -1;
        while (offset <= current.Length)
        {
            var index = current.IndexOf(selection, offset, System.StringComparison.Ordinal);
            if (index < 0) break;
            if (firstIndex < 0) firstIndex = index;
            count++;
            offset = index + selection.Length;
        }

        if (count == 0)
        {
            error = "選択テキストがファイル内に見つかりません。";
            return false;
        }

        if (count > 1)
        {
            error = $"選択テキストが {count} 件一致しました。より広い範囲を選択して一意にしてください。";
            return false;
        }

        proposed = current[..firstIndex] + newText + current[(firstIndex + selection.Length)..];
        return true;
    }
}
