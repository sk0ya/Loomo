using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 小型ローカルLLMが構造化された tool_calls ではなく本文テキストとしてツール呼び出しを書いてしまった場合に、
/// それを <see cref="ToolUse"/> へ変換するパーサ。Ollama・ONNX(Phi-4) いずれの本文出力にも使える。
/// 対応形式：関数呼び出し風 <c>run_powershell("...")</c>、引数 JSON オブジェクト <c>{"command":"..."}</c>、
/// およびその配列 <c>[{"name":...,"arguments":{...}}]</c>（小モデルが arguments だけ／別名キー／
/// コードフェンス付き／配列で吐くことがある）。Phi-4-mini の tool call も後者の JSON 配列形式で返る。
/// JSON 配列／オブジェクトに明示的な <c>name</c> があれば <c>run_powershell</c> 以外（<c>write_file</c>/
/// <c>edit_file</c> 等）も復元し、引数はそのまま通す（canonical 化は各ツールの NormalizeArguments の責務）。
/// </summary>
public static class ToolCallTextParser
{
    /// <summary>
    /// 本文テキストからツール呼び出しを取り出す。検出できなければ空配列を返す。
    /// 複数要素の配列はその数だけツール呼び出しを返す。
    /// </summary>
    public static IReadOnlyList<ToolUse> Parse(string text)
    {
        var s = StripToolWrapper(StripCodeFence(text.Trim()));

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

        // 小型モデルが配列の先頭 "[" だけを落として {"name":...}] と出すことがある。
        // その場合は配列として補正すると通常の JSON tool call と同じ経路で扱える。
        if (s.StartsWith('{') && s.EndsWith(']'))
            return ParseJsonToolCalls("[" + s, text);

        // {"command":...} の先頭 "{" だけを落として "command":...} と出すことがある。
        // 唯一の tool 引数 JSON として補正できる形だけ扱う。
        if (s.StartsWith("\"command\":", StringComparison.Ordinal) && s.EndsWith('}'))
            return ParseJsonToolCalls("{" + s, text);

        // 小型モデルは「実行します:」等の前置き/後置き文を混ぜてから JSON tool call を書くことがある。
        // そのまま最終回答扱いにするとツールが呼ばれないため、本文中の最初の JSON オブジェクト/配列を
        // tool call らしいキーが含まれる場合に限って救済する。
        var embedded = TryExtractFirstJsonValue(s);
        if (embedded is not null && LooksLikeToolJson(embedded))
            return ParseJsonToolCalls(embedded, text);

        return Array.Empty<ToolUse>();
    }

    /// <summary>JSON オブジェクト／配列からツール呼び出しを取り出す。引数未検出の要素は無視する。
    /// 明示的に <c>run_powershell</c> 以外の <c>name</c> がある要素は、引数をそのまま通す
    /// （各ツールの <see cref="IAgentTool.NormalizeArguments"/> が後段でキー揺れを吸収する）。
    /// それ以外（run_powershell・名前なしの復元形）は command を canonical 化して載せる。</summary>
    private static IReadOnlyList<ToolUse> ParseJsonToolCalls(string json, string rawText)
    {
        var node = TryParseNode(json);
        if (node is null)
        {
            // 配列全体が不正（小モデルは2件目以降の content 等を壊しやすい）。先頭の 1 オブジェクトだけでも
            // 救えるか試す（文字列を意識した波括弧マッチで先頭の {…} を取り出す）。1ターン1ツールの規律とも整合し、
            // 先頭が正しければそれを実行して次ターンへ進める（全部巻き添えで捨てない）。
            var first = TryExtractFirstObject(json);
            node = first is null ? null : TryParseNode(first);
            if (node is null) return Array.Empty<ToolUse>();
        }

        var elements = node is JsonArray arr ? arr : new JsonArray { node };
        var result = new List<ToolUse>();
        foreach (var el in elements)
        {
            if (el is not JsonObject obj) continue;

            var name = GetStringValue(obj, "name");
            if (!string.IsNullOrEmpty(name) && name != PwshContract.ToolName)
            {
                var argsJson = ExtractArgsObject(obj).ToJsonString();
                result.Add(new ToolUse(Guid.NewGuid().ToString("N"), name, argsJson, rawText));
                continue;
            }

            if (ExtractCommandFromJson(obj) is { } command)
                result.Add(MakeToolUse(command, rawText));
        }
        return result;
    }

    /// <summary>ツール引数オブジェクトを取り出す。<c>arguments</c>/<c>parameters</c> ラップがあればその中、
    /// 無ければ <c>name</c>/<c>description</c> を除いた残りを引数とみなす。</summary>
    private static JsonObject ExtractArgsObject(JsonObject obj)
    {
        if (obj["arguments"] is JsonObject args) return args;
        if (obj["parameters"] is JsonObject parameters) return parameters;

        var rest = new JsonObject();
        foreach (var kv in obj)
            if (kv.Key is not ("name" or "description"))
                rest[kv.Key] = kv.Value?.DeepClone();
        return rest;
    }

    private static string? GetStringValue(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? TryParseNode(string s)
    {
        try { return JsonNode.Parse(s); }
        catch { /* 下のフォールバック補修へ */ }

        // フォールバック：小型モデルが壊しやすい2系統のJSON誤りを限定補修して再パースする。
        //  (1) キー代入のタイプミス: "content="… のように ":" を "=" と書く（write_file で頻出）。
        //  (2) 無効なバックスラッシュエスケープ: \. \s \d .\src を JSON 文字列に素で書く（正規表現/パス）。
        // (1)→(2) の順に適用（先に ":" を復元してから文字列境界を見てエスケープを直す）。
        var repaired = RepairInvalidEscapes(RepairKeyAssignmentTypo(s));
        if (repaired != s)
        {
            try { return JsonNode.Parse(repaired); }
            catch { /* 補修しても駄目なら諦める */ }
        }
        return null;
    }

    /// <summary>正規キー（command/content/path/old_string/new_string）の直後が <c>=</c> になっている
    /// タイプミス <c>"content="…</c> を、本来の <c>"content":"…</c> へ補修する。
    /// 正しい JSON では key の後は必ず <c>:</c> なので、<c>"知っているキー名="</c> は曖昧さなくこの誤りに限られる。</summary>
    private static string RepairKeyAssignmentTypo(string s)
        => KeyTypoPattern.Replace(s, "\"$1\":\"");

    private static readonly Regex KeyTypoPattern =
        new("\"(command|content|path|old_string|new_string)=\"", RegexOptions.Compiled);

    /// <summary>JSON 文字列リテラル内の無効なバックスラッシュエスケープを <c>\\</c> へ二重化して有効化する。
    /// 有効なエスケープ（<c>\" \\ \/ \b \f \n \r \t</c> と <c>\uXXXX</c>）はそのまま残す。
    /// 文字列の外（構造部分）は一切触らない。補修不要ならば入力をそのまま返す（参照一致で判定可能）。</summary>
    private static string RepairInvalidEscapes(string s)
    {
        StringBuilder? sb = null;   // 変更が無ければ確保せず原文を返す
        var inStr = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!inStr)
            {
                sb?.Append(c);
                if (c == '"') inStr = true;
                continue;
            }
            if (c == '"') { sb?.Append(c); inStr = false; continue; }
            if (c == '\\')
            {
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                var valid = next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't'
                            || (next == 'u' && i + 5 < s.Length && IsHex(s, i + 2, 4));
                if (valid)
                {
                    var take = next == 'u' ? 6 : 2;
                    sb?.Append(s, i, take);
                    i += take - 1;
                    continue;
                }
                // 無効：バックスラッシュを二重化（次の文字は次反復で通常どおり処理）。
                sb ??= new StringBuilder(s.Length + 8).Append(s, 0, i);
                sb.Append('\\').Append('\\');
                continue;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }

    private static bool IsHex(string s, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (i >= s.Length) return false;
            var c = s[i];
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>文字列リテラルを意識して波括弧の対応を取り、先頭で完結する <c>{…}</c> を返す（無ければ null）。
    /// 文字列内の <c>{</c>/<c>}</c>/<c>"</c>（<c>\"</c> エスケープ込み）は数えない。</summary>
    private static string? TryExtractFirstObject(string s)
    {
        var depth = 0;
        var inStr = false;
        var esc = false;
        var start = -1;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': if (depth++ == 0) start = i; break;
                case '}':
                    if (--depth == 0 && start >= 0)
                        return s[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    /// <summary>本文中の最初の JSON オブジェクトまたは配列を、文字列リテラルを意識して取り出す。</summary>
    private static string? TryExtractFirstJsonValue(string s)
    {
        var start = -1;
        var open = '\0';
        var close = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '{' or '[')
            {
                start = i;
                open = s[i];
                close = open == '{' ? '}' : ']';
                break;
            }
        }
        if (start < 0) return null;

        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }

            if (c == '"')
            {
                inStr = true;
                continue;
            }
            if (c == open) depth++;
            else if (c == close && --depth == 0)
                return s[start..(i + 1)];
        }
        return null;
    }

    private static bool LooksLikeToolJson(string json)
        => json.Contains("\"name\"", StringComparison.Ordinal)
           || json.Contains("\"arguments\"", StringComparison.Ordinal)
           || json.Contains("\"parameters\"", StringComparison.Ordinal)
           || json.Contains("\"command\"", StringComparison.Ordinal);

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

    /// <summary>Phi-4-mini が tool call 用特殊トークンで JSON を包むことがあるため、本文判定前に剥がす。
    /// <c>&lt;|tool_response|&gt;</c> は本来は結果側トークンだが、小モデルが誤って tool call に使う場合がある。</summary>
    private static string StripToolWrapper(string s)
    {
        foreach (var (start, end) in new[]
                 {
                     ("<|tool_call|>", "<|/tool_call|>"),
                     ("<|tool_response|>", "<|/tool_response|>")
                 })
        {
            if (!s.StartsWith(start, StringComparison.Ordinal)) continue;
            var inner = s[start.Length..];
            var endIndex = inner.IndexOf(end, StringComparison.Ordinal);
            if (endIndex >= 0)
                inner = inner[..endIndex];
            return inner.Trim();
        }
        return s;
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
