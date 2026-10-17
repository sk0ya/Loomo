using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>Claude Code のセッション1つの要約（§38.4）。<see cref="ContextTokens"/> は最後の応答を作ったときに
/// モデルへ渡した量（入力＋キャッシュ読み書き）＝いまのコンテキストの大きさ。</summary>
public sealed record ClaudeSessionSummary(
    string SessionId, string Path, DateTime LastWriteUtc, string? Title, string? LastPrompt, long? ContextTokens, string? Model);

/// <summary>ワークスペースの Claude Code セッションを並べ、題名・最後の頼みごと・コンテキスト量を読む（§38.4）。
/// 記録の場所探しは <see cref="ClaudeTranscriptLocator"/>（ハーネスペインと同じ）。</summary>
public static class ClaudeSessionCatalog
{
    public static IReadOnlyList<ClaudeSessionSummary> List(IWorkspaceService workspace, int max = 20, string? projectsRoot = null)
    {
        var root = projectsRoot ?? ClaudeTranscriptLocator.DefaultProjectsRoot();
        return ClaudeTranscriptLocator.FindProjectDirectories(root, workspace)
            .SelectMany(dir => ClaudeTranscriptLocator.ListSessions(dir, DateTime.MinValue))
            .OrderByDescending(file => file.LastWriteUtc)
            .Take(max)
            .Select(file => Summarize(file.SessionId, file.Path, file.LastWriteUtc, ClaudeTranscriptLocator.ReadLines(file.Path)))
            .ToList();
    }

    public static ClaudeSessionSummary? Read(string transcriptPath)
    {
        if (!File.Exists(transcriptPath))
            return null;
        return Summarize(System.IO.Path.GetFileNameWithoutExtension(transcriptPath), transcriptPath,
            File.GetLastWriteTimeUtc(transcriptPath), ClaudeTranscriptLocator.ReadLines(transcriptPath));
    }

    /// <summary>記録の行から要約を作る。題名は <c>ai-title</c>（Claude が付けたもの、最後の1つ）、
    /// 最後の頼みごとは <c>last-prompt</c>、コンテキスト量は最後の <c>assistant</c> の usage。</summary>
    public static ClaudeSessionSummary Summarize(string sessionId, string path, DateTime lastWriteUtc, IEnumerable<string> lines)
    {
        string? title = null, lastPrompt = null, model = null;
        long? context = null;
        foreach (var line in lines)
        {
            // 全行を JSON として開くと重いので、関係する行だけ開く。
            if (line.Contains("\"ai-title\"", StringComparison.Ordinal))
                title = ReadString(line, "aiTitle") ?? title;
            else if (line.Contains("\"last-prompt\"", StringComparison.Ordinal))
                lastPrompt = ReadString(line, "lastPrompt") ?? lastPrompt;
            else if (line.Contains("\"usage\"", StringComparison.Ordinal) && line.Contains("\"assistant\"", StringComparison.Ordinal)
                     && ReadUsage(line) is { } usage)
                (context, model) = (usage.Tokens, usage.Model ?? model);
        }
        return new ClaudeSessionSummary(sessionId, path, lastWriteUtc, title, lastPrompt, context, model);
    }

    private static string? ReadString(string line, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static (long Tokens, string? Model)? ReadUsage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || !message.TryGetProperty("usage", out var usage))
                return null;
            long Get(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            var tokens = Get("input_tokens") + Get("cache_read_input_tokens") + Get("cache_creation_input_tokens");
            var model = message.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return tokens > 0 ? (tokens, model) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>「123k」のような短い表記。</summary>
    public static string FormatTokens(long tokens)
        => tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M"
            : tokens >= 1000 ? $"{tokens / 1000.0:0}k"
            : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
