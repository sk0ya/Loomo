using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// APIキー不要のローカルスタブ。実LLMの代わりに簡易ルールで応答し、ツールループの動作確認に使う。
/// 規約: 直近のユーザー発話が「$ で始まる」→ run_command、「open &lt;path&gt;」→ open_in_editor、
/// 「ls [path]」→ list_directory。それ以外はエコー。
/// </summary>
public sealed class StubAiClient : IAiClient
{
    public AiProvider Provider => AiProvider.Stub;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        // 直前がツール結果なら、それを要約して終了（無限ループ防止）
        var last = conversation.Messages.LastOrDefault();
        if (last is { Role: ChatRole.Tool })
        {
            const string done = "（Stub）ツールの実行が完了しました。";
            foreach (var chunk in Chunk(done)) yield return new TextDelta(chunk);
            yield break;
        }

        var userText = conversation.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Trim() ?? "";

        if (userText.StartsWith("$"))
        {
            var cmd = userText[1..].Trim();
            yield return new ToolUseRequested(new ToolUse(NewId(), "run_command",
                JsonSerializer.Serialize(new { command = cmd })));
            yield break;
        }
        if (userText.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
        {
            var path = userText[5..].Trim();
            yield return new ToolUseRequested(new ToolUse(NewId(), "open_in_editor",
                JsonSerializer.Serialize(new { path })));
            yield break;
        }
        if (userText.Equals("ls", StringComparison.OrdinalIgnoreCase) ||
            userText.StartsWith("ls ", StringComparison.OrdinalIgnoreCase))
        {
            var path = userText.Length > 2 ? userText[3..].Trim() : "";
            yield return new ToolUseRequested(new ToolUse(NewId(), "list_directory",
                JsonSerializer.Serialize(new { path })));
            yield break;
        }

        var reply = $"（Stubエージェント）受信: 「{userText}」\n" +
                    "実LLMに接続するには設定でプロバイダを Claude/OpenAI/Local に変更してください。\n" +
                    "テスト用コマンド: `$ <cmd>` / `open <path>` / `ls [path]`";
        foreach (var chunk in Chunk(reply)) yield return new TextDelta(chunk);
    }

    private static string NewId() => "stub_" + Guid.NewGuid().ToString("N")[..8];

    private static IEnumerable<string> Chunk(string text, int size = 12)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
