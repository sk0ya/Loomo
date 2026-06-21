using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// ワークフローの1ステップ。ユーザーが手で並べる「単発のAI指示」。
/// <see cref="UseTools"/> が true ならツール（全登録ツール）を使えるエージェントループで、
/// false なら単発のテキスト応答で実行される。
/// </summary>
public sealed class WorkflowStep
{
    /// <summary>表示名（任意）。空でもよい。</summary>
    public string Title { get; set; } = "";

    /// <summary>AIへの指示文。<c>{{1}}</c> / <c>{{prev}}</c> / <c>{{all}}</c> で前段の出力を差し込める。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>このステップでツールを使う（true=エージェントループ／false=テキストのみ単発）。</summary>
    public bool UseTools { get; set; }
}

/// <summary>名前付きワークフロー（ステップの並び）。</summary>
public sealed class Workflow
{
    /// <summary>永続化ID。未保存なら null。</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = "";

    public List<WorkflowStep> Steps { get; set; } = new();
}

/// <summary>
/// ステップ指示文のプレースホルダを前段の出力で置換する純粋関数。
/// <list type="bullet">
///   <item><c>{{1}}</c>…<c>{{N}}</c> — N 番目（1始まり）のステップ出力。</item>
///   <item><c>{{prev}}</c> — 直前ステップの出力。</item>
///   <item><c>{{all}}</c> — それまでの全ステップ出力を見出し付きで連結。</item>
/// </list>
/// 範囲外の番号は空文字へ。プレースホルダが無ければ指示文はそのまま（＝前段出力は自動添付しない）。
/// </summary>
public static class WorkflowPrompt
{
    private static readonly Regex Token = new(@"\{\{\s*(\d+|prev|all)\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Resolve(string prompt, IReadOnlyList<string> previousOutputs)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt ?? "";

        return Token.Replace(prompt, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
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
