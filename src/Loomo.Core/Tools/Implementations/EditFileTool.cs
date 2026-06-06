using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>
/// 既存ファイル中の <c>old_string</c> を <c>new_string</c> へ一意置換する構造化ツール。
/// old_string は対象ファイルに<b>完全一致しちょうど 1 箇所</b>であることを要求し、0／複数なら綺麗なエラーを返す
/// （壊れた <c>-replace</c> でファイルを黙って破損させない＝小モデルが外しても安全に再試行できる）。
/// パスは <see cref="IWorkspaceService.ResolvePath"/> でワークスペースルート配下へ確定し、編集後はエディタで開く。
/// </summary>
public sealed class EditFileTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    private readonly IEditorService _editor;

    public EditFileTool(IWorkspaceService workspace, IEditorService editor)
    {
        _workspace = workspace;
        _editor = editor;
    }

    public string Name => EditFileContract.ToolName;
    public bool RequiresApproval => true;

    public ToolDefinition Definition => new(
        Name,
        "Replace text in an existing file. old_string must match the file exactly and be unique; new_string is the replacement (empty to delete).",
        ToolDefinition.ObjectSchema(
            (EditFileContract.PathArg, "string", "Workspace-relative or absolute file path.", true),
            (EditFileContract.OldArg, "string", "Exact existing text to replace (must be unique in the file).", true),
            // new_string は削除（空置換）も許すため required にしない（required string は minLength 1 が付く）。
            (EditFileContract.NewArg, "string", "Replacement text (omit or empty to delete old_string).", false)));

    public string DescribeInvocation(JsonElement args)
    {
        var path = args.GetString(EditFileContract.PathArg);
        var oldStr = args.GetString(EditFileContract.OldArg);
        var newStr = args.GetString(EditFileContract.NewArg);
        return $"edit_file: {path}（「{FileToolText.Preview(oldStr)}」→「{FileToolText.Preview(newStr)}」）";
    }

    /// <summary>path/old_string/new_string の別名キーを吸収して canonical な引数へ寄せる。</summary>
    public JsonElement NormalizeArguments(JsonElement arguments)
    {
        var path = arguments.GetStringAny(EditFileContract.PathKeys);
        var oldStr = arguments.GetStringAny(EditFileContract.OldKeys);
        var newStr = arguments.GetStringAny(EditFileContract.NewKeys);
        return JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [EditFileContract.PathArg] = path,
            [EditFileContract.OldArg] = oldStr,
            [EditFileContract.NewArg] = newStr,
        });
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString(EditFileContract.PathArg);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error(
                "path が空です。arguments に {\"path\":..,\"old_string\":..,\"new_string\":..} を入れて呼び出してください。");

        var oldStr = args.GetString(EditFileContract.OldArg);
        if (string.IsNullOrEmpty(oldStr))
            return ToolResult.Error("old_string が空です。置換したい既存の文字列を指定してください（新規作成は write_file を使う）。");

        var newStr = args.GetString(EditFileContract.NewArg);

        string resolved;
        try { resolved = _workspace.ResolvePath(path); }
        catch (UnauthorizedAccessException ex) { return ToolResult.Error(ex.Message); }

        if (!File.Exists(resolved))
            return ToolResult.Error($"ファイルが存在しません: {resolved}（新規作成は write_file を使ってください）。");

        string content;
        try { content = await File.ReadAllTextAsync(resolved, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"読み込みに失敗しました: {ex.Message}"); }

        var count = FileToolText.CountOccurrences(content, oldStr);
        if (count == 0)
            return ToolResult.Error(
                "old_string が見つかりませんでした。対象ファイルの実際の文字列をそのまま（空白・改行込みで）コピーして指定してください。");
        if (count > 1)
            return ToolResult.Error(
                $"old_string が {count} 箇所一致しました。一意に特定できるよう前後の行を含めて長めに指定してください。");

        var updated = FileToolText.ReplaceFirst(content, oldStr, newStr);
        try { await File.WriteAllTextAsync(resolved, updated, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"書き込みに失敗しました: {ex.Message}"); }

        try { await _editor.OpenFileAsync(resolved); } catch { /* 表示は best-effort */ }

        return ToolResult.Ok($"編集完了: {resolved}（1 箇所置換）");
    }
}
