using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>非AIツールステップ1件の実行結果。<see cref="Output"/> は後段へ <c>{{prev}}</c> 等で渡る本文、
/// <see cref="Summary"/> は実行ログ向けの短い説明。</summary>
public sealed record WorkflowToolResult(string Output, bool Ok, string Summary);

/// <summary>
/// ワークフローの「ツールステップ」（非AI）を決定論的に実行するエンジン。UI 非依存・LLM 非依存で、
/// ワークフロー実行を AI から分離する核。種別ごとに既存サービス（<see cref="ITerminalService"/> /
/// <see cref="IWorkspaceService"/> / <see cref="IEditorService"/>）と <see cref="ISafetyPolicy"/> を用いる。
/// 危険コマンドのブロック判定はここで行い、承認カードの提示は呼び出し側（ViewModel）が担う。
/// </summary>
public sealed class WorkflowToolRunner
{
    private readonly ITerminalService _terminal;
    private readonly IWorkspaceService _workspace;
    private readonly IEditorService _editor;
    private readonly ISafetyPolicy _safety;

    public WorkflowToolRunner(
        ITerminalService terminal,
        IWorkspaceService workspace,
        IEditorService editor,
        ISafetyPolicy safety)
    {
        _terminal = terminal;
        _workspace = workspace;
        _editor = editor;
        _safety = safety;
    }

    /// <summary>この種別が実行前にユーザー承認を要するか（Command / WriteFile は副作用があるため要承認）。</summary>
    public static bool RequiresApproval(WorkflowStepKind kind)
        => kind is WorkflowStepKind.Command or WorkflowStepKind.WriteFile;

    /// <summary>承認カード等に出すツール名（既存ツールの語彙に寄せる）。</summary>
    public static string ToolNameFor(WorkflowStepKind kind) => kind switch
    {
        WorkflowStepKind.Command => PwshContract.ToolName,
        WorkflowStepKind.WriteFile => WriteFileContract.ToolName,
        WorkflowStepKind.ReadFile => "read_file",
        WorkflowStepKind.Transform => "transform",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>承認カード本文。Command はコマンド行、WriteFile はパスと行数を示す。</summary>
    public string DescribeApproval(WorkflowStep step, string resolvedPrimary, string resolvedContent)
        => step.Kind switch
        {
            WorkflowStepKind.Command => $"$ {resolvedPrimary}",
            WorkflowStepKind.WriteFile =>
                $"write_file: {resolvedPrimary}（{CountLines(resolvedContent)}行を書き込み）",
            _ => resolvedPrimary,
        };

    /// <summary>ツールステップを実行する。<paramref name="resolvedPrimary"/> はプレースホルダ展開済みの主テキスト
    /// （Command=コマンド / ReadFile・WriteFile=パス / Transform=入力）、<paramref name="resolvedContent"/> は
    /// 展開済みの <see cref="WorkflowStep.Content"/>（WriteFile=内容 / Transform=置換後）。</summary>
    public async Task<WorkflowToolResult> RunAsync(
        WorkflowStep step, string resolvedPrimary, string resolvedContent, CancellationToken ct)
        => step.Kind switch
        {
            WorkflowStepKind.Command => await RunCommandAsync(resolvedPrimary, ct),
            WorkflowStepKind.ReadFile => await RunReadFileAsync(resolvedPrimary),
            WorkflowStepKind.WriteFile => await RunWriteFileAsync(resolvedPrimary, resolvedContent, ct),
            WorkflowStepKind.Transform => RunTransform(step, resolvedPrimary, resolvedContent),
            _ => new WorkflowToolResult("", false, $"未対応のステップ種別です: {step.Kind}"),
        };

    private async Task<WorkflowToolResult> RunCommandAsync(string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new WorkflowToolResult("", false, "コマンドが空です。");

        // AI ステップと同じ安全評価（危険コマンドのブロックリスト）を通す。引数は canonical 化済みの形で渡す。
        var decision = _safety.Evaluate(PwshContract.ToolName, CommandArgs(command));
        if (decision.Blocked)
            return new WorkflowToolResult("", false, decision.Reason ?? "コマンドはブロックされました。");

        var result = await _terminal.RunCommandAsync(MakeNonInteractive(command), ct);
        var summary = result.Success
            ? $"コマンド完了（exit {result.ExitCode}）"
            : $"コマンド失敗（exit {result.ExitCode}）";
        // 出力は stdout/stderr をそのまま後段へ渡す（{{prev}} がきれいなテキストになる）。
        return new WorkflowToolResult(result.Output.TrimEnd(), result.Success, summary);
    }

    private async Task<WorkflowToolResult> RunReadFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new WorkflowToolResult("", false, "ファイルパスが空です。");

        string resolved;
        try { resolved = _workspace.ResolvePath(path); }
        catch (UnauthorizedAccessException ex) { return new WorkflowToolResult("", false, ex.Message); }

        if (!File.Exists(resolved))
            return new WorkflowToolResult("", false, $"ファイルが存在しません: {resolved}");

        try
        {
            var content = await _workspace.ReadFileAsync(resolved);
            return new WorkflowToolResult(content, true, $"読込: {resolved}（{CountLines(content)}行）");
        }
        catch (Exception ex)
        {
            return new WorkflowToolResult("", false, $"読込に失敗しました: {ex.Message}");
        }
    }

    private async Task<WorkflowToolResult> RunWriteFileAsync(string path, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new WorkflowToolResult("", false, "ファイルパスが空です。");

        string resolved;
        try { resolved = _workspace.ResolvePath(path); }
        catch (UnauthorizedAccessException ex) { return new WorkflowToolResult("", false, ex.Message); }

        try
        {
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(resolved, content, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new WorkflowToolResult("", false, $"書き込みに失敗しました: {ex.Message}"); }

        try { await _editor.OpenFileAsync(resolved); } catch { /* 表示は best-effort */ }

        var summary = $"書き込み完了: {resolved}（{CountLines(content)}行 / {Encoding.UTF8.GetByteCount(content)} bytes）";
        // 後段で参照できるよう、出力には書き込んだ内容自体を渡す。
        return new WorkflowToolResult(content, true, summary);
    }

    private static WorkflowToolResult RunTransform(WorkflowStep step, string input, string replacement)
    {
        if (string.IsNullOrEmpty(step.Pattern))
            return new WorkflowToolResult(input, true, "検索パターンが空のため変換しませんでした。");

        try
        {
            var output = step.IsRegex
                ? Regex.Replace(input, step.Pattern, replacement)
                : input.Replace(step.Pattern, replacement);
            return new WorkflowToolResult(output, true,
                step.IsRegex ? "正規表現で置換しました。" : "テキストを置換しました。");
        }
        catch (ArgumentException ex)
        {
            return new WorkflowToolResult("", false, $"正規表現が不正です: {ex.Message}");
        }
    }

    /// <summary>安全評価へ渡す canonical な引数 <c>{"command":"..."}</c> を組み立てる。</summary>
    private static JsonElement CommandArgs(string command)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { [PwshContract.CommandArg] = command });

    /// <summary>git をページャ無し・非対話に寄せる（<see cref="Tools.Implementations.PwshTool"/> と同方針）。</summary>
    private static string MakeNonInteractive(string command)
    {
        var trimmedStart = command.TrimStart();
        if (!trimmedStart.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
            return command;
        if (trimmedStart.StartsWith("git --no-pager ", StringComparison.OrdinalIgnoreCase)
            || trimmedStart.StartsWith("git -P ", StringComparison.OrdinalIgnoreCase))
            return command;

        var leading = command[..(command.Length - trimmedStart.Length)];
        return leading + "git --no-pager " + trimmedStart[4..];
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        return count;
    }
}
