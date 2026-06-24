using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// ワークフローステップの種別。<see cref="Ai"/> は従来どおり <c>AgentOrchestrator</c> 経由で LLM を呼ぶ。
/// それ以外は LLM を一切使わず <see cref="WorkflowToolRunner"/> で決定論的に実行する「ツールステップ」。
/// </summary>
public enum WorkflowStepKind
{
    /// <summary>AIへの単発指示（既定）。</summary>
    Ai,

    /// <summary>PowerShell コマンドを実行し stdout を出力にする。</summary>
    Command,

    /// <summary>ファイルを読み、その本文を出力にする。</summary>
    ReadFile,

    /// <summary>テンプレート内容をファイルへ書き出す。</summary>
    WriteFile,

    /// <summary>入力テキストへ検索置換（リテラル/正規表現）を施し、結果を出力にする。</summary>
    Transform,
}

/// <summary>
/// ワークフローの1ステップ。ユーザーが手で並べる「単発の指示」。<see cref="Kind"/> が <see cref="WorkflowStepKind.Ai"/>
/// なら LLM 呼び出し、それ以外は決定論的なツール実行。ウォームアップ済みプレフィックスを再利用するため、
/// AI ステップ実行時にモデルへ提示するツール定義はチャットと同一に保つ。
/// </summary>
public sealed class WorkflowStep
{
    /// <summary>表示名（任意）。空でもよい。</summary>
    public string Title { get; set; } = "";

    /// <summary>ステップ種別。既定は <see cref="WorkflowStepKind.Ai"/>（旧データ互換）。</summary>
    public WorkflowStepKind Kind { get; set; } = WorkflowStepKind.Ai;

    /// <summary>各種別の「主テキスト」。<c>{{1}}</c> / <c>{{prev}}</c> / <c>{{all}}</c> / <c>{{input}}</c> で
    /// 前段の出力やワークフロー入力を差し込める。
    /// 種別ごとの意味: Ai=指示文 / Command=コマンド行 / ReadFile・WriteFile=パス / Transform=入力テキスト。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>WriteFile=書き込む内容テンプレート、Transform=置換後文字列。他種別では未使用。
    /// プレースホルダ展開の対象。</summary>
    public string Content { get; set; } = "";

    /// <summary>Transform=検索パターン（<see cref="IsRegex"/> が真なら正規表現）。他種別では未使用。</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Transform で <see cref="Pattern"/> を正規表現として扱うか。</summary>
    public bool IsRegex { get; set; }
}

/// <summary>名前付きワークフロー（ステップの並び）。</summary>
public sealed class Workflow
{
    /// <summary>永続化ID。未保存なら null。</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = "";

    public List<WorkflowStep> Steps { get; set; } = new();

    /// <summary>いずれかのステップが <c>{{input}}</c> を使う（＝実行時にワークフロー入力を必要とする）か。
    /// FolderTree／エディタのコンテキストメニューから「入力ありワークフロー」だけを出すための判定に使う。</summary>
    public bool UsesInput =>
        Steps.Any(s => WorkflowPrompt.UsesInput(s.Prompt) || WorkflowPrompt.UsesInput(s.Content));
}

/// <summary>
/// ステップ指示文のプレースホルダを前段の出力・ワークフロー入力で置換する純粋関数。
/// <list type="bullet">
///   <item><c>{{input}}</c> — 実行時にユーザーが渡したワークフロー入力。ファイル入力ではパス、テキスト入力では本文。</item>
///   <item><c>{{input.path}}</c> / <c>{{input.content}}</c> / <c>{{input.name}}</c> / <c>{{input.relativePath}}</c> — 構造化入力の各フィールド。</item>
///   <item><c>{{1}}</c>…<c>{{N}}</c> — N 番目（1始まり）のステップ出力。</item>
///   <item><c>{{prev}}</c> — 直前ステップの出力。</item>
///   <item><c>{{all}}</c> — それまでの全ステップ出力を見出し付きで連結。</item>
/// </list>
/// 範囲外の番号・未指定の入力は空文字へ。プレースホルダが無ければ指示文はそのまま（＝前段出力は自動添付しない）。
/// </summary>
public static class WorkflowPrompt
{
    private static readonly Regex Token = new(@"\{\{\s*(\d+|prev|all|input(?:\.(?:path|content|name|relativePath|kind))?)\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InputToken = new(@"\{\{\s*input(?:\.(?:path|content|name|relativePath|kind))?\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InputContentToken = new(@"\{\{\s*input\.content\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>テキストに <c>{{input}}</c> プレースホルダが含まれるか。</summary>
    public static bool UsesInput(string? text) =>
        !string.IsNullOrEmpty(text) && InputToken.IsMatch(text);

    /// <summary>テキストに <c>{{input.content}}</c> プレースホルダが含まれるか。</summary>
    public static bool UsesInputContent(string? text) =>
        !string.IsNullOrEmpty(text) && InputContentToken.IsMatch(text);

    public static string Resolve(string prompt, IReadOnlyList<string> previousOutputs, string? input = null)
        => Resolve(prompt, previousOutputs, WorkflowRunInput.FromText(input ?? ""));

    public static string Resolve(string prompt, IReadOnlyList<string> previousOutputs, WorkflowRunInput? input)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt ?? "";

        return Token.Replace(prompt, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            if (key == "input")
                return input?.PrimaryText ?? "";
            if (key == "input.path")
                return input?.Path ?? "";
            if (key == "input.content")
                return input?.Content ?? "";
            if (key == "input.name")
                return input?.Name ?? "";
            if (key == "input.relativepath")
                return input?.RelativePath ?? "";
            if (key == "input.kind")
                return input?.Kind.ToString() ?? "";
            if (key == "prev")
                return previousOutputs.Count > 0 ? previousOutputs[^1] : "";
            if (key == "all")
                return JoinAll(previousOutputs);

            // 数字（1始まり）
            if (int.TryParse(key, out var n) && n >= 1 && n <= previousOutputs.Count)
                return previousOutputs[n - 1];
            return "";   // 範囲外・未実行
        });
    }

    private static string JoinAll(IReadOnlyList<string> outputs)
    {
        if (outputs.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < outputs.Count; i++)
        {
            if (i > 0) sb.Append('\n').Append('\n');
            sb.Append("【ステップ").Append(i + 1).Append("の出力】\n").Append(outputs[i]);
        }
        return sb.ToString();
    }
}

public enum WorkflowRunInputKind
{
    Text,
    File,
}

/// <summary>ワークフロー実行時の構造化入力。<see cref="PrimaryText"/> は旧 <c>{{input}}</c> 互換の値。</summary>
public sealed record WorkflowRunInput(
    WorkflowRunInputKind Kind,
    string PrimaryText,
    string? Text = null,
    string? Path = null,
    string? RelativePath = null,
    string? Name = null,
    string? Content = null)
{
    public static WorkflowRunInput FromText(string text) =>
        new(WorkflowRunInputKind.Text, text ?? "", Text: text ?? "", Content: text ?? "");

    public static WorkflowRunInput FromFile(string path, string? relativePath = null, string? content = null)
    {
        var normalized = path ?? "";
        return new(
            WorkflowRunInputKind.File,
            normalized,
            Path: normalized,
            RelativePath: relativePath,
            Name: string.IsNullOrEmpty(normalized) ? null : System.IO.Path.GetFileName(normalized),
            Content: content);
    }

    public WorkflowRunInput WithContent(string content) => this with { Content = content ?? "" };
}
