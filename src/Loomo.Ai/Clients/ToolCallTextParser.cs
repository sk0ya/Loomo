using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 小型ローカルLLMが構造化された tool_calls ではなく本文テキストとしてツール呼び出しを書いてしまった場合に、
/// それを <see cref="ToolUse"/> へ変換するパーサ。Ollama・ONNX(Phi-4) いずれの本文出力にも使える。
/// 対応形式：関数呼び出し風 <c>run_powershell("...")</c>、引数 JSON オブジェクト <c>{"command":"..."}</c>、
/// およびその配列 <c>[{"name":...,"arguments":{...}}]</c>（小モデルが arguments だけ／別名キー／
/// コードフェンス付き／配列で吐くことがある）。Phi-4-mini の tool call も後者の JSON 配列形式で返る。
/// </summary>
public static class ToolCallTextParser
{
    /// <summary>
    /// 本文テキストからツール呼び出しを取り出す。検出できなければ空配列を返す。
    /// 複数要素の配列はその数だけツール呼び出しを返す。
    /// </summary>
    public static IReadOnlyList<ToolUse> Parse(string text)
    {
        var s = StripCodeFence(text.Trim());

        // 形式1: run_powershell("コマンド")
        const string prefix = "run_powershell(\"";
        const string suffix = "\")";
        if (s.StartsWith(prefix, StringComparison.Ordinal) && s.EndsWith(suffix, StringComparison.Ordinal))
            return new[] { MakeToolUse(s[prefix.Length..^suffix.Length], text) };

        // 形式2: run_powershell {"command":"..."} / run_powershell [{"arguments":{...}}]
        const string jsonPrefix = "run_powershell ";
        if (s.StartsWith(jsonPrefix, StringComparison.Ordinal))
        {
            var json = s[jsonPrefix.Length..].Trim();
            if ((json.StartsWith('{') && json.EndsWith('}')) || (json.StartsWith('[') && json.EndsWith(']')))
                return ParseJsonToolCalls(json, text);
        }

        // 形式3/4: JSON オブジェクト {...} または配列 [{...}, ...]
        if ((s.StartsWith('{') && s.EndsWith('}')) || (s.StartsWith('[') && s.EndsWith(']')))
            return ParseJsonToolCalls(s, text);

        return Array.Empty<ToolUse>();
    }

    /// <summary>JSON オブジェクト／配列からツール呼び出しを取り出す。コマンド未検出の要素は無視する。</summary>
    private static IReadOnlyList<ToolUse> ParseJsonToolCalls(string json, string rawText)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return Array.Empty<ToolUse>(); }

        var elements = node is JsonArray arr ? arr : new JsonArray { node };
        var result = new List<ToolUse>();
        foreach (var el in elements)
            if (el is JsonObject obj && ExtractCommandFromJson(obj) is { } command)
                result.Add(MakeToolUse(command, rawText));
        return result;
    }

    private static ToolUse MakeToolUse(string command, string rawText)
    {
        var argsJson = new JsonObject { [PwshContract.CommandArg] = command }.ToJsonString();
        return new ToolUse(Guid.NewGuid().ToString("N"), PwshContract.ToolName, argsJson, rawText);
    }

    /// <summary>前後の Markdown コードフェンス（```／```json …）を剥がす。無ければ原文を返す。</summary>
    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal) || !s.EndsWith("```", StringComparison.Ordinal))
            return s;
        var inner = s[3..^3];
        var nl = inner.IndexOf('\n');           // 開きフェンスの言語指定（```json 等）を落とす
        if (nl >= 0) inner = inner[(nl + 1)..];
        return inner.Trim();
    }

    /// <summary>引数 JSON オブジェクトからコマンド文字列を取り出す。<c>arguments</c>/<c>parameters</c> ラップと別名キーに対応。見つからなければ null。</summary>
    private static string? ExtractCommandFromJson(JsonObject obj)
    {
        // {"name":"run_powershell","arguments":{...}} のように引数が入れ子なら中を見る。
        if (obj["arguments"] is JsonObject nested) obj = nested;
        // 小型モデルはツール定義の parameters と実引数を混同し、
        // {"name":"run_powershell","parameters":{"command":"..."}} と返すことがある。
        else if (obj["parameters"] is JsonObject parameters) obj = parameters;

        foreach (var key in PwshContract.CommandKeys)
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var command))
                return command;
        return null;
    }
}
