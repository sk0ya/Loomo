using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// Claude Code のセッション記録（<c>~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl</c>）を1つの
/// <see cref="HarnessRun"/> に読む（§37.2.1）。I/O は持たない——行の列を受け取るだけなので、テストは文字列で書ける。
/// <para><b>usage は <c>message.id</c> ごとに1回だけ数える</b>。assistant の応答は content ブロックごとに別の行へ
/// 分かれて書かれ、どの行も同じ usage を持つ。行ごとに足すとトークンが数倍に化ける。</para>
/// </summary>
public static class ClaudeTranscriptParser
{
    private const int SummaryMax = 160;
    private const int ErrorHeadMax = 200;

    private static readonly Regex PersistedSize = new(
        @"Output too large \((?<n>[\d.,]+)\s*(?<u>KB|MB|B)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>1セッションを読む。<paramref name="subagents"/> は <c>&lt;session&gt;/subagents/*.jsonl</c>。</summary>
    public static HarnessRun Parse(
        string runId,
        string transcriptPath,
        IEnumerable<string> lines,
        IEnumerable<(string Path, IEnumerable<string> Lines)>? subagents = null)
    {
        var state = new ParseState();
        ReadFile(state, transcriptPath, lines, isSubagent: false);
        var subagentCount = 0;
        if (subagents is not null)
        {
            foreach (var (path, subLines) in subagents)
            {
                subagentCount++;
                ReadFile(state, path, subLines, isSubagent: true);
            }
        }

        var calls = state.Calls.Select((b, i) => b.Build(i)).ToList();
        var usage = state.UsageByMessage.Values.Aggregate(default(RunTokenUsage), (sum, u) => sum + u);
        var title = !string.IsNullOrWhiteSpace(state.Title)
            ? state.Title!
            : state.Prompts.FirstOrDefault() is { } first ? Shorten(first, 60) : ShortId(runId);

        return new HarnessRun
        {
            Id = runId,
            TranscriptPath = transcriptPath,
            Title = title,
            StartedAt = state.First,
            EndedAt = state.Last,
            Cwd = state.Cwd,
            Models = state.Models.ToList(),
            Usage = usage,
            CostUsd = state.Cost,
            ToolCalls = calls,
            Prompts = state.Prompts,
            PromptMarks = state.PromptMarks,
            Commands = state.Commands,
            SkillsUsed = state.Skills,
            AgentsUsed = state.Agents,
            SubagentCount = subagentCount,
            ToolErrorCount = calls.Count(c => c.IsError),
            ToolCallCountExcludingRejected = calls.Count(c => !c.IsRejected),
        };
    }

    /// <summary>記録の先頭から <c>cwd</c> を探す（どのフォルダーのセッションかを確かめるため、§37.2.1）。</summary>
    public static string? ReadCwd(IEnumerable<string> lines, int maxLines = 40)
    {
        foreach (var line in lines.Take(maxLines))
        {
            if (line.IndexOf("\"cwd\"", StringComparison.Ordinal) < 0)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                    return cwd.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static void ReadFile(ParseState state, string path, IEnumerable<string> lines, bool isSubagent)
    {
        var pending = new Dictionary<string, CallBuilder>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // 書きかけの最終行など
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;
                var type = Str(root, "type");
                var at = Time(root);
                if (at is { } t && type is "user" or "assistant" or "system")
                {
                    if (state.First is null || t < state.First) state.First = t;
                    if (state.Last is null || t > state.Last) state.Last = t;
                }
                if (!isSubagent && state.Cwd is null && Str(root, "cwd") is { Length: > 0 } cwd)
                    state.Cwd = cwd;

                switch (type)
                {
                    case "ai-title" when !isSubagent:
                        state.Title = Str(root, "aiTitle") ?? state.Title;
                        break;
                    case "cost-state" when !isSubagent:
                        if (root.TryGetProperty("totalCostUSD", out var cost) && cost.ValueKind == JsonValueKind.Number
                            && cost.TryGetDecimal(out var usd))
                            state.Cost = usd;
                        break;
                    case "assistant":
                        ReadAssistant(state, pending, root, path, lineNumber, at, isSubagent);
                        break;
                    case "user":
                        ReadUser(state, pending, root, isSubagent, lineNumber, at);
                        break;
                    // 作業の途中に人間が足した指示（Claude はターンを終えずに取り込む）。user の行にはならず、
                    // queued_command の添付として残る——これも人間の頼みごとで、ここから先の操作はこれに応えたもの。
                    case "attachment" when !isSubagent
                        && root.TryGetProperty("attachment", out var attachment) && attachment.ValueKind == JsonValueKind.Object
                        && Str(attachment, "type") == "queued_command" && Str(attachment, "commandMode") is null or "prompt":
                        AddPrompt(state, Str(attachment, "prompt"), new RunPromptMark(lineNumber, at));
                        break;
                }
            }
        }
    }

    private static void ReadAssistant(
        ParseState state, Dictionary<string, CallBuilder> pending, JsonElement root,
        string path, int lineNumber, DateTimeOffset? at, bool isSubagent)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return;
        if (Str(message, "model") is { Length: > 0 } model && !model.StartsWith('<'))
            state.Models.Add(model);
        if (Str(message, "id") is { Length: > 0 } id && message.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            // 同じ id の行は同じ応答の別ブロック。最後に見た値で上書きする（足さない）。
            state.UsageByMessage[(isSubagent ? "s:" : "m:") + id] = new RunTokenUsage(
                Long(usage, "input_tokens"), Long(usage, "output_tokens"),
                Long(usage, "cache_read_input_tokens"), Long(usage, "cache_creation_input_tokens"));
        }
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Str(block, "type") != "tool_use")
                continue;
            var name = Str(block, "name") ?? "?";
            var input = block.TryGetProperty("input", out var i) ? i : default;
            var builder = new CallBuilder
            {
                LineNumber = lineNumber,
                TranscriptPath = path,
                At = at,
                Tool = name,
                FromSubagent = isSubagent,
                InputChars = input.ValueKind == JsonValueKind.Undefined ? 0 : input.GetRawText().Length,
            };
            Describe(builder, name, input);
            if (name == "Skill" && input.ValueKind == JsonValueKind.Object
                && (Str(input, "skill") ?? Str(input, "command")) is { Length: > 0 } skill)
                state.Skills.Add(skill);
            if (name is "Agent" or "Task" && input.ValueKind == JsonValueKind.Object)
                state.Agents.Add(Str(input, "subagent_type") is { Length: > 0 } kind ? kind : "general-purpose");
            state.Calls.Add(builder);
            if (Str(block, "id") is { Length: > 0 } toolUseId)
                pending[toolUseId] = builder;
        }
    }

    private static void ReadUser(
        ParseState state, Dictionary<string, CallBuilder> pending, JsonElement root, bool isSubagent,
        int lineNumber, DateTimeOffset? at)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
            return;
        var isMeta = root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;

        if (content.ValueKind == JsonValueKind.String)
        {
            if (!isSubagent && !isMeta)
                AddPrompt(state, content.GetString(), new RunPromptMark(lineNumber, at));
            return;
        }
        if (content.ValueKind != JsonValueKind.Array)
            return;

        var hadToolResult = false;
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            switch (Str(block, "type"))
            {
                case "tool_result":
                    hadToolResult = true;
                    if (Str(block, "tool_use_id") is { } id && pending.Remove(id, out var call))
                        FillResult(call, block);
                    break;
                case "text":
                    text.Append(Str(block, "text"));
                    break;
            }
        }
        if (!hadToolResult && !isSubagent && !isMeta)
            AddPrompt(state, text.ToString(), new RunPromptMark(lineNumber, at));
    }

    private static readonly Regex CommandName =
        new(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline);

    private static readonly Regex CommandArgs =
        new(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline);

    private static void AddPrompt(ParseState state, string? text, RunPromptMark mark)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var trimmed = text.TrimStart();
        // スラッシュコマンドは指示（題）にはしないが、打たれた名前と引数は持っておく
        // （/ticket-run VCI-001 の引数で Run をチケットに結ぶ、§37.11）。
        if (trimmed.StartsWith("<command-", StringComparison.Ordinal)
            && CommandName.Match(trimmed) is { Success: true } name)
        {
            var args = CommandArgs.Match(trimmed) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
            state.Commands.Add(new HarnessCommandInvocation(name.Groups[1].Value.Trim(), args));
        }
        // スラッシュコマンドの記録・ローカルコマンドの出力・中断の印は人間の指示ではない。
        if (trimmed.StartsWith("<local-command", StringComparison.Ordinal)
            || trimmed.StartsWith("<command-", StringComparison.Ordinal)
            || trimmed.StartsWith("<bash-", StringComparison.Ordinal)
            || trimmed.StartsWith("[Request interrupted", StringComparison.Ordinal))
            return;
        state.Prompts.Add(text.Trim());
        state.PromptMarks.Add(mark);
    }

    private static void FillResult(CallBuilder call, JsonElement block)
    {
        var text = ResultText(block);
        call.HasResult = true;
        var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        if (text.StartsWith("The user doesn't want to proceed", StringComparison.Ordinal)
            || text.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        {
            call.IsRejected = true;
            isError = false;
        }
        call.IsError = isError;
        call.ResultChars = text.Length;
        if (text.Contains("<persisted-output>", StringComparison.Ordinal))
        {
            call.OutputPersisted = true;
            if (PersistedSize.Match(text) is { Success: true } m
                && double.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                var bytes = m.Groups["u"].Value switch { "MB" => n * 1024 * 1024, "KB" => n * 1024, _ => n };
                call.ResultChars = (int)Math.Min(int.MaxValue, bytes);
            }
        }
        if (isError)
            call.ErrorHead = Shorten(text, ErrorHeadMax);
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && Str(item, "type") == "text")
                sb.Append(Str(item, "text"));
        }
        return sb.ToString();
    }

    private static void Describe(CallBuilder call, string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            call.Summary = tool;
            return;
        }
        var file = Str(input, "file_path") ?? Str(input, "notebook_path");
        var command = Str(input, "command");
        if (tool is "Bash" or "PowerShell" && command is not null)
        {
            call.Command = command;
            call.Summary = Shorten(command, SummaryMax);
            return;
        }
        if (file is not null)
        {
            call.FilePath = file;
            call.Summary = file;
            ReadEditText(call, tool, input);
            if (tool == "Read" && input.TryGetProperty("offset", out var offset)
                && offset.ValueKind == JsonValueKind.Number && offset.TryGetInt64(out var o) && o > 0)
            {
                call.ReadOffset = o;
                call.Summary = $"{file}（{o} 行目から）";
            }
            return;
        }
        call.Summary = Shorten(
            Str(input, "pattern") ?? Str(input, "query") ?? Str(input, "skill") ?? Str(input, "description")
            ?? Str(input, "url") ?? Str(input, "prompt") ?? tool,
            SummaryMax);
    }

    /// <summary>
    /// Edit／MultiEdit の置換前後の文字列（上限つき）。表の行の書き換え（<c>tickets.tsv</c> の Status 等）を読むため。
    /// MultiEdit は各置換を改行でつなぐ。
    /// </summary>
    private static void ReadEditText(CallBuilder call, string tool, JsonElement input)
    {
        if (tool == "Edit")
        {
            call.EditOld = Cap(Str(input, "old_string"));
            call.EditNew = Cap(Str(input, "new_string"));
        }
        else if (tool == "MultiEdit" && input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            var items = edits.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            call.EditOld = Cap(string.Join('\n', items.Select(e => Str(e, "old_string") ?? "")));
            call.EditNew = Cap(string.Join('\n', items.Select(e => Str(e, "new_string") ?? "")));
        }

        static string? Cap(string? text) => text is null ? null : text.Length <= MaxEditTextChars ? text : text[..MaxEditTextChars];
    }

    /// <summary>置換文字列を持つ上限。Run はキャッシュに積まれるので、大きな置換を丸ごと抱えない。</summary>
    private const int MaxEditTextChars = 4000;

    /// <summary>空白（改行含む）を1つに畳んで <paramref name="max"/> 字で切る。</summary>
    public static string Shorten(string text, int max)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..(max - 1)] + "…";
    }

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static DateTimeOffset? Time(JsonElement root)
        => Str(root, "timestamp") is { } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : null;

    private sealed class ParseState
    {
        public string? Title;
        public decimal? Cost;
        public string? Cwd;
        public DateTimeOffset? First;
        public DateTimeOffset? Last;
        public readonly Dictionary<string, RunTokenUsage> UsageByMessage = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Models = new(StringComparer.Ordinal);
        public readonly List<CallBuilder> Calls = new();
        public readonly List<string> Prompts = new();
        public readonly List<RunPromptMark> PromptMarks = new();
        public readonly List<HarnessCommandInvocation> Commands = new();
        public readonly List<string> Skills = new();
        public readonly List<string> Agents = new();
    }

    private sealed class CallBuilder
    {
        public int LineNumber;
        public string TranscriptPath = "";
        public DateTimeOffset? At;
        public string Tool = "";
        public string Summary = "";
        public string? FilePath;
        public long? ReadOffset;
        public string? Command;
        public string? EditOld;
        public string? EditNew;
        public int InputChars;
        public bool HasResult;
        public bool IsError;
        public bool IsRejected;
        public int ResultChars;
        public bool OutputPersisted;
        public string? ErrorHead;
        public bool FromSubagent;

        public RunToolCall Build(int index) => new()
        {
            Index = index,
            LineNumber = LineNumber,
            TranscriptPath = TranscriptPath,
            At = At,
            Tool = Tool,
            Summary = Summary,
            FilePath = FilePath,
            ReadOffset = ReadOffset,
            Command = Command,
            EditOld = EditOld,
            EditNew = EditNew,
            InputChars = InputChars,
            HasResult = HasResult,
            IsError = IsError,
            IsRejected = IsRejected,
            ResultChars = ResultChars,
            OutputPersisted = OutputPersisted,
            ErrorHead = ErrorHead,
            FromSubagent = FromSubagent,
        };
    }
}
