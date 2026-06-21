using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>トランスクリプト行の文字列整形ヘルパー（チャット＝<see cref="AiBarViewModel"/> と
/// ワークフロー＝<see cref="WorkflowViewModel"/> で共有するツールカード／結果の整形）。</summary>
internal static class TranscriptFormatting
{
    public static string Truncate(string s, int max = 2000)
        => s.Length <= max ? s : s[..max] + $"\n…(+{s.Length - max} 文字)";

    /// <summary>ツール使用エントリのヘッダー（折りたたんでも常時見える1行）。
    /// 「🔧 ツール名: 主要引数」の形で、何をしたかが一目で分かるようにする。</summary>
    public static string ToolUseHeader(string toolName, string argumentsJson)
    {
        var summary = SummarizeToolArgs(toolName, argumentsJson);
        return string.IsNullOrEmpty(summary) ? $"🔧 {toolName}" : $"🔧 {toolName}: {summary}";
    }

    /// <summary>ツールの引数JSONから代表引数を1つ抜き出し1行要約に整える（各 *Contract の別名配列を順に見る）。</summary>
    public static string SummarizeToolArgs(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";

            var keys = toolName switch
            {
                PwshContract.ToolName => PwshContract.CommandKeys,
                WriteFileContract.ToolName => WriteFileContract.PathKeys,
                EditFileContract.ToolName => EditFileContract.PathKeys,
                WebSearchContract.ToolName => WebSearchContract.QueryKeys,
                _ => null
            };

            if (keys is not null)
                foreach (var k in keys)
                    if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                        return OneLine(v.GetString() ?? "");

            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    return OneLine(p.Value.GetString() ?? "");
        }
        catch { /* パース不能なら要約なし */ }
        return "";
    }

    /// <summary>改行・連続空白を畳んで1行にし、長ければ先頭から切って省略記号を付ける。</summary>
    public static string OneLine(string text, int max = 80)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>ツールカードの本文（narration があれば引数JSONの上に併記）。</summary>
    public static string ComposeToolCard(string? narration, string argumentsJson, string? rawJson)
    {
        var text = narration?.Trim();
        var raw = rawJson?.Trim();
        var body = string.IsNullOrEmpty(text)
            ? "arguments:" + Environment.NewLine + argumentsJson
            : text + Environment.NewLine + "arguments:" + Environment.NewLine + argumentsJson;

        if (!string.IsNullOrEmpty(raw) && raw != argumentsJson)
            body += Environment.NewLine + "raw:" + Environment.NewLine + raw;

        return body;
    }

    /// <summary>承認サマリが統合差分（+/- 接頭辞付きの行）を含むか。</summary>
    public static bool ContainsDiff(string summary)
    {
        foreach (var line in summary.AsSpan().EnumerateLines())
            if (line.Length > 0 && (line[0] == '+' || line[0] == '-'))
                return true;
        return false;
    }

    public static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{elapsed.TotalMilliseconds:0} ms";
        if (elapsed.TotalMinutes < 1)
            return $"{elapsed.TotalSeconds:0.0} 秒";
        return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
    }
}
