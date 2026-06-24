using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    // 引数JSONを組み直す際、非ASCII（日本語パス・本文）を \uXXXX へ化けさせないため relaxed エンコーダを使う。
    // 既定エンコーダだと ArgumentsJson が「アイデア」のようになり、トレース/UI の可読性とトークン量が悪化する。
    private static readonly JsonSerializerOptions JsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 本文テキストからツール呼び出しを取り出す。検出できなければ空配列を返す。
    /// 複数要素の配列はその数だけツール呼び出しを返す。
    /// </summary>
    public static IReadOnlyList<ToolUse> Parse(string text)
    {
        // Qwen3 等の thinking ブロックは本文判定の前に取り除く（中の文章を tool call/最終回答と誤認しない）。
        text = StripThinkBlocks(text);

        // Qwen3（Hermes 形式）は <tool_call>{…}</tool_call> を 1 件以上出す。先にこれを拾う
        // （複数ブロックをすべて復元する。Phi-4 の <|tool_call|> パイプ記法・素の配列は従来経路で扱う）。
        var hermes = ParseHermesToolCalls(text);
        if (hermes.Count > 0) return hermes;

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

    /// <summary>Qwen3（Hermes 形式）の <c>&lt;tool_call&gt;…&lt;/tool_call&gt;</c> ブロックをすべて取り出し、
    /// 各ブロック内の JSON を 1 ツール呼び出しへ変換する。閉じタグが無い末尾ブロックは行末まで（=全文末尾まで）を
    /// 中身とみなして救済する。<c>&lt;tool_call&gt;</c> を含まなければ空を返し、従来経路に委ねる。</summary>
    private static IReadOnlyList<ToolUse> ParseHermesToolCalls(string text)
    {
        const string open = "<tool_call>";
        const string close = "</tool_call>";
        if (!text.Contains(open, StringComparison.Ordinal)) return Array.Empty<ToolUse>();

        var result = new List<ToolUse>();
        var i = 0;
        while (true)
        {
            var start = text.IndexOf(open, i, StringComparison.Ordinal);
            if (start < 0) break;
            start += open.Length;

            var end = text.IndexOf(close, start, StringComparison.Ordinal);
            var inner = end < 0 ? text[start..] : text[start..end];
            result.AddRange(ParseJsonToolCalls(StripCodeFence(inner.Trim()), text));

            i = end < 0 ? text.Length : end + close.Length;
        }
        return result;
    }

    /// <summary>thinking ブロック <c>&lt;think&gt;…&lt;/think&gt;</c> を取り除く（Qwen3 を no_think で動かしても
    /// 空ブロックが残ることがあり、tool call/最終回答の判定や本文へ混ざるのを防ぐ）。閉じていない単独の
    /// <c>&lt;think&gt;</c> は、後続にツール呼び出し等の実体が続きうるため触らない。</summary>
    public static string StripThinkBlocks(string text)
        => string.IsNullOrEmpty(text) ? text : ThinkPattern.Replace(text, "").Trim();

    private static readonly Regex ThinkPattern =
        new("<think>.*?</think>", RegexOptions.Compiled | RegexOptions.Singleline);

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
                var argsObj = ExtractArgsObject(obj);
                RestorePathBackslashes(argsObj);
                var argsJson = argsObj.ToJsonString(JsonOptions);
                result.Add(new ToolUse(Guid.NewGuid().ToString("N"), name, argsJson, rawText));
                continue;
            }

            if (ExtractCommandFromJson(obj) is { } command)
                result.Add(MakeToolUse(RestoreControlEscapes(command), rawText));
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
        //  (1) キー代入のタイプミス: "content="… や "old_string",… のように ":" を "=" / "," と書く。
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

    /// <summary>正規キー（command/content/path/old_string/new_string）と値の間の区切りが <c>:</c> でない
    /// 2種のタイプミスを補修する。(1) <c>"content="…</c>（<c>:</c> を <c>=</c>＝キー閉じ引用符も落とす）→ <c>"content":"…</c>。
    /// (2) <c>"old_string","…</c>（キーは閉じたが <c>:</c> を <c>,</c> と書く。Qwen3 の <c>&lt;tool_call&gt;</c> で実測）→ <c>"old_string":"…</c>。
    /// 正しい JSON では key の後は必ず <c>:</c> なので、<b>既知キー名に続く <c>="</c> / <c>","</c></b> は曖昧さなくこの誤りに限られる
    /// （しかもこの補修は <c>JsonNode.Parse</c> が失敗した本文にのみ走る＝正常 JSON は触らない）。</summary>
    private static string RepairKeyAssignmentTypo(string s)
        => KeyCommaTypoPattern.Replace(KeyEqualsTypoPattern.Replace(s, "\"$1\":\""), "\"$1\":\"");

    private static readonly Regex KeyEqualsTypoPattern =
        new("\"(command|content|path|old_string|new_string)=\"", RegexOptions.Compiled);
    private static readonly Regex KeyCommaTypoPattern =
        new("\"(command|content|path|old_string|new_string)\",\"", RegexOptions.Compiled);

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

    /// <summary>引数オブジェクトの <c>path</c> 値に紛れ込んだ制御文字をバックスラッシュ表記へ戻す
    /// （<see cref="RestoreControlEscapes"/> 参照）。ファイルパスに生のタブ・改行は入らないため安全。
    /// <c>content</c>/<c>old_string</c>/<c>new_string</c> は本物の改行・タブを含みうるので触らない。</summary>
    private static void RestorePathBackslashes(JsonObject args)
    {
        if (args["path"] is JsonValue v && v.TryGetValue<string>(out var path))
        {
            var restored = RestoreControlEscapes(path);
            if (!ReferenceEquals(restored, path)) args["path"] = restored;
        }
    }

    /// <summary>JSON文字列として解釈された結果に紛れ込んだ制御文字を元のバックスラッシュ表記へ戻す。
    /// モデルが Windows パス（<c>C:\temp\notes.md</c> 等）を素の単一バックスラッシュで書くと、<c>\t \n \r \b \f</c>
    /// は<b>有効な</b>JSONエスケープなので <see cref="JsonNode.Parse"/> が「成功」してタブ・改行へ化け、
    /// <see cref="RepairInvalidEscapes"/>（失敗時のみ走る）では救えない。JSONは文字列中に生の制御文字を許さない
    /// ＝<c>command</c>/<c>path</c> 値にこれらが現れたらモデルがパス区切りのバックスラッシュを書いた証左なので、
    /// 安全に <c>\t</c> 等へ戻せる。制御文字が無ければ参照同一の入力をそのまま返す。</summary>
    private static string RestoreControlEscapes(string s)
    {
        if (s.IndexOfAny(ControlEscapeChars) < 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static readonly char[] ControlEscapeChars = { '\t', '\n', '\r', '\b', '\f' };

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
        var argsJson = new JsonObject { [PwshContract.CommandArg] = command }.ToJsonString(JsonOptions);
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
