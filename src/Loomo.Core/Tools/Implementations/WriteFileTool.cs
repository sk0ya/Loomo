using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>
/// ファイルを新規作成／全置換する構造化ツール。内容を <c>content</c> 引数で直接受け取るため、
/// PowerShell でファイルを書く場合の二重エスケープ（PS 構文＋JSON）を避けられる。
/// パスは <see cref="IWorkspaceService.ResolvePath"/> でワークスペースルート配下へ確定（パストラバーサル防止）、
/// 承認カード（<see cref="DescribeInvocation"/>）に行数差分を出し、書き込み後はエディタで開いて結果を可視化する。
/// </summary>
public sealed class WriteFileTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    private readonly IEditorService _editor;

    public WriteFileTool(IWorkspaceService workspace, IEditorService editor)
    {
        _workspace = workspace;
        _editor = editor;
    }

    public string Name => WriteFileContract.ToolName;
    public bool RequiresApproval => true;   // 書き込みなので承認必須

    public ToolDefinition Definition => new(
        Name,
        "Create a new file or fully overwrite an existing one with the given content.",
        ToolDefinition.ObjectSchema(
            (WriteFileContract.PathArg, "string", "Workspace-relative or absolute file path.", true),
            (WriteFileContract.ContentArg, "string", "Full file content to write.", true)));

    public string DescribeInvocation(JsonElement args)
    {
        var path = args.GetString(WriteFileContract.PathArg);
        var content = args.GetString(WriteFileContract.ContentArg);
        var newLines = FileToolText.CountLines(content);
        int? oldLines = null;
        try
        {
            var resolved = _workspace.ResolvePath(path);
            if (File.Exists(resolved))
                oldLines = FileToolText.CountLines(File.ReadAllText(resolved));
        }
        catch { /* 承認カードの要約なので best-effort。ルート外・IO 失敗時は新規扱いで表示する。 */ }

        return oldLines is { } o
            ? $"write_file: {path}（{o}行 → {newLines}行・上書き）"
            : $"write_file: {path}（新規 {newLines}行）";
    }

    /// <summary>path/content の別名キーを吸収して canonical な <c>{"path":..,"content":..}</c> へ寄せる。</summary>
    public JsonElement NormalizeArguments(JsonElement arguments)
    {
        var path = arguments.GetStringAny(WriteFileContract.PathKeys);
        var content = arguments.GetStringAny(WriteFileContract.ContentKeys);
        return JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [WriteFileContract.PathArg] = path,
            [WriteFileContract.ContentArg] = content,
        });
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString(WriteFileContract.PathArg);
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error(
                "path が空です。arguments に {\"path\":\"<相対/絶対パス>\",\"content\":\"<内容>\"} を入れて呼び出してください。");

        var content = args.GetString(WriteFileContract.ContentArg);

        string resolved;
        try { resolved = _workspace.ResolvePath(path); }
        catch (UnauthorizedAccessException ex) { return ToolResult.Error(ex.Message); }

        try
        {
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(resolved, content, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"書き込みに失敗しました: {ex.Message}"); }

        try { await _editor.OpenFileAsync(resolved); } catch { /* 表示は best-effort */ }

        return ToolResult.Ok(
            $"書き込み完了: {resolved}（{FileToolText.CountLines(content)}行 / {Encoding.UTF8.GetByteCount(content)} bytes）");
    }
}
