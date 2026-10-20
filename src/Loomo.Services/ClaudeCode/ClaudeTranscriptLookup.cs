using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// ツリーで選んだ操作1つの全文を、記録から<b>その時だけ</b>引き直す（§39.9）。digest は数 MB の記録を追いかけ続けるので、
/// コマンドの出力・Write の中身・サブエージェントの報告の全文までは抱えない——行番号と tool_use の id だけ持っておき、
/// 選ばれたときにその行を読む。読むのは裏のスレッドから。
/// </summary>
public static class ClaudeTranscriptLookup
{
    /// <summary>引き直す全文の上限（字）。見せ直しの出力（§39.5.1）と同じ桁。</summary>
    public const int MaxChars = 1_000_000;

    /// <summary>結果（tool_result）の本文。まだ結果が無い・読めなければ null。</summary>
    public static string? ReadResult(RunToolCall call)
    {
        if (call.ResultLineNumber <= 0 || call.ToolUseId is not { } id
            || ReadLine(call.TranscriptPath, call.ResultLineNumber) is not { } line)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && Str(block, "type") == "tool_result" && Str(block, "tool_use_id") == id)
                    return Cap(ResultText(block));
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>入力（tool_use の input）の文字列の欄（Write の <c>content</c>、Agent の <c>prompt</c> など）。</summary>
    public static string? ReadInput(RunToolCall call, string field)
    {
        if (call.ToolUseId is not { } id || ReadLine(call.TranscriptPath, call.LineNumber) is not { } line)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && Str(block, "type") == "tool_use" && Str(block, "id") == id
                    && block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    return Str(input, field) is { } value ? Cap(value) : null;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>記録の <paramref name="lineNumber"/> 行目（1始まり）。書き込み中でも読む。</summary>
    internal static string? ReadLine(string path, int lineNumber)
    {
        if (lineNumber <= 0)
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var number = 0;
            while (reader.ReadLine() is { } line)
            {
                if (++number == lineNumber)
                    return line;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join("\n", content.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && Str(item, "type") == "text")
            .Select(item => Str(item, "text") ?? ""));
    }

    private static string Cap(string text) => text.Length <= MaxChars ? text : text[^MaxChars..];

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
