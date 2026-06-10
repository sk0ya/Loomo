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
public sealed class EditFileTool : IAgentTool, IFileMutationTool
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

    // edit_file は対象箇所だけの置換／追記なので「破壊的全文上書き」ではない（多段の絞り込み編集は正当）。
    public bool FullyOverwritesTarget => false;

    /// <summary>canonical な絶対パスへ解決（相対／絶対を同一キーへ寄せる）。ルート外・未指定は null。</summary>
    public string? ResolveTargetPath(JsonElement normalizedArguments)
    {
        var path = normalizedArguments.GetString(EditFileContract.PathArg);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return _workspace.ResolvePath(path); }
        catch { return null; }
    }

    // 注: old_string が空のとき末尾追記する挙動は実装に持つが、ツール定義の説明文には載せない。
    // 説明文を増やすと小モデルが write 過多（読み取りタスクでも write_file）に傾く非局所回帰が出たため、
    // 「モデルが追記で自然に空 old_string を投げる」習性を実行側で受け止めるに留める（プロンプト無摂動）。
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

        // old_string 空＝末尾追記。小モデルの「追記」の自然な手（空 old_string での edit_file）を
        // エラーにせず意図どおり成立させる。既存内容が改行で終わっていなければ改行を1つ補い、追記行が
        // 前の行に連結しないようにする（new_string が改行始まりなら二重化しない）。
        if (string.IsNullOrEmpty(oldStr))
        {
            if (string.IsNullOrEmpty(newStr))
                return ToolResult.Error("old_string も new_string も空です。追記する内容を new_string に指定してください。");

            var sep = content.Length > 0 && !content.EndsWith("\n") && !newStr.StartsWith("\n") ? "\n" : "";
            try { await File.WriteAllTextAsync(resolved, content + sep + newStr, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return ToolResult.Error($"書き込みに失敗しました: {ex.Message}"); }

            try { await _editor.OpenFileAsync(resolved); } catch { /* 表示は best-effort */ }
            return ToolResult.Ok($"追記完了: {resolved}（末尾に {newStr.Length} 文字を追記）");
        }

        var count = FileToolText.CountOccurrences(content, oldStr);
        if (count == 0)
        {
            // 大文字小文字・改行コードだけが違う近傍候補があれば、ファイル上の実テキストを添えて返す。
            // 「実際の文字列をコピーして」だけだと小モデルは再読せず推測のまま諦めて虚偽の完了報告に
            // 流れるため（multi-file-bump の主要故障）、復旧をそのまま複写するだけの作業にする。
            var near = FileToolText.FindNearMatch(content, oldStr);
            if (near is not null)
                return ToolResult.Error(
                    "old_string が見つかりませんでした。大文字小文字・改行コード・エスケープだけが異なる箇所が"
                    + "あります。次の実際のテキストを一字一句そのまま old_string に指定して再実行してください:\n" + near);
            return ToolResult.Error(
                "old_string が見つかりませんでした。対象ファイルの実際の文字列をそのまま（空白・改行込みで）コピーして指定してください。");
        }
        if (count > 1)
        {
            // 「長めに指定して」「読み直して」だけだと小モデルは読んだ後も同じ短い old_string を再送して
            // 反復上限で死ぬ（実測）。一致を含む実際の行をエラーに列挙し、復旧を「行をコピーして
            // 1箇所ずつ置換」する機械的な作業に変える。全置換の別経路（write_file）も案内する。
            var lines = FileToolText.LinesContaining(content, oldStr, max: 3);
            var lineHint = lines.Count > 0
                ? "一致を含む行: " + string.Join(" ", lines.ConvertAll(l => "「" + l + "」"))
                  + "。行全体を old_string にコピーして1箇所ずつ置換してください。"
                : "実際のテキストの前後を含めてコピーして一意にしてください。";
            return ToolResult.Error(
                $"old_string が {count} 箇所一致しました。{lineHint}"
                + "全部の箇所を置き換えたい場合は、write_file で全文を書き換えても構いません。");
        }

        var updated = FileToolText.ReplaceFirst(content, oldStr, newStr);
        try { await File.WriteAllTextAsync(resolved, updated, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"書き込みに失敗しました: {ex.Message}"); }

        try { await _editor.OpenFileAsync(resolved); } catch { /* 表示は best-effort */ }

        return ToolResult.Ok($"編集完了: {resolved}（1 箇所置換）");
    }
}
