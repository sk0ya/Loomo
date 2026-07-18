using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// AI ファイル変更ジャーナル（<see cref="FileChangeJournal"/>）と、
/// <see cref="AgentOrchestrator"/> がファイル変更ツールの成功時に前後全文を記録する挙動の検証。
/// Diff セッション（AI変更レビュー・巻き戻し）のデータ源になる。
/// </summary>
public class FileChangeJournalTests
{
    [Fact]
    public void Record_and_remove_raise_changed_and_update_snapshot()
    {
        var journal = new FileChangeJournal();
        var changed = 0;
        journal.Changed += (_, _) => changed++;

        journal.Record(MakeRecord("/ws/a.txt", isNew: true, oldContent: null, newContent: "A"));
        journal.Record(MakeRecord("/ws/b.txt", isNew: false, oldContent: "old", newContent: "new"));
        Assert.Equal(2, changed);
        Assert.Equal(2, journal.Snapshot().Count);

        // パス指定の削除は大文字小文字を無視する（canonical パスの揺れ対策）
        journal.RemoveForPath("/WS/A.TXT");
        Assert.Equal(3, changed);
        Assert.Equal("/ws/b.txt", Assert.Single(journal.Snapshot()).Path);

        journal.Clear();
        Assert.Equal(4, changed);
        Assert.Empty(journal.Snapshot());

        // 空のジャーナルの Clear は Changed を発火しない（無駄な再描画をしない）
        journal.Clear();
        Assert.Equal(4, changed);
    }

    [Fact]
    public async Task Orchestrator_records_before_and_after_content_on_successful_file_mutation()
    {
        var dir = Directory.CreateTempSubdirectory("loomo-journal-test").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            var journal = new FileChangeJournal();
            var tools = new ToolRegistry(new IAgentTool[] { new DiskWriteTool() });

            // 1ターンで新規作成 → 同一ファイルを上書き（write→write は上書きガード対象外）。
            var ai = new ScriptedAiClient(
                new AgentEvent[]
                {
                    new ToolUseRequested(new ToolUse("w1", "write_file",
                        JsonSerializer.Serialize(new { path, content = "hello" }))),
                    new ToolUseRequested(new ToolUse("w2", "write_file",
                        JsonSerializer.Serialize(new { path, content = "hello world" }))),
                },
                new AgentEvent[] { new TextDelta("書きました。") });

            var orch = new AgentOrchestrator(
                new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
                NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance,
                journal: journal);

            await foreach (var _ in orch.RunTurnAsync(new Conversation(), "ファイルを書いて")) { }

            var records = journal.Snapshot();
            Assert.Equal(2, records.Count);

            // 1回目：新規作成（変更前なし）
            Assert.True(records[0].IsNew);
            Assert.Null(records[0].OldContent);
            Assert.Equal("hello", records[0].NewContent);
            Assert.Equal("write_file", records[0].ToolName);
            Assert.Equal(path, records[0].Path);

            // 2回目：上書き（変更前=直前の内容）
            Assert.False(records[1].IsNew);
            Assert.Equal("hello", records[1].OldContent);
            Assert.Equal("hello world", records[1].NewContent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Orchestrator_does_not_record_failed_or_unchanged_mutations()
    {
        var dir = Directory.CreateTempSubdirectory("loomo-journal-test").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "same");
            var journal = new FileChangeJournal();
            var tools = new ToolRegistry(new IAgentTool[] { new DiskWriteTool() });

            // 失敗する呼び出し（fail=true）と、内容が変わらない上書き → どちらも記録されない。
            var ai = new ScriptedAiClient(
                new AgentEvent[]
                {
                    new ToolUseRequested(new ToolUse("w1", "write_file",
                        JsonSerializer.Serialize(new { path, content = "x", fail = true }))),
                    new ToolUseRequested(new ToolUse("w2", "write_file",
                        JsonSerializer.Serialize(new { path, content = "same" }))),
                },
                new AgentEvent[] { new TextDelta("done") });

            var orch = new AgentOrchestrator(
                new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
                NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance,
                journal: journal);

            await foreach (var _ in orch.RunTurnAsync(new Conversation(), "書いて")) { }

            Assert.Empty(journal.Snapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static FileChangeRecord MakeRecord(string path, bool isNew, string? oldContent, string? newContent)
        => new(DateTimeOffset.Now, "s1", "t1", "write_file", path, isNew, oldContent, newContent);

    /// <summary>実ディスクへ書くファイル変更ツール（前後全文の読み取りを本物の I/O で検証するため）。</summary>
    private sealed class DiskWriteTool : IAgentTool, IFileMutationTool
    {
        public string Name => "write_file";
        public bool FullyOverwritesTarget => true;
        public ToolDefinition Definition => new(Name, "disk write tool", new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;

        public string? ResolveTargetPath(JsonElement args)
            => args.TryGetProperty("path", out var p) ? p.GetString() : null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            if (args.TryGetProperty("fail", out var f) && f.GetBoolean())
                return Task.FromResult(ToolResult.Error("意図的な失敗"));
            File.WriteAllText(ResolveTargetPath(args)!, args.GetProperty("content").GetString() ?? "");
            return Task.FromResult(ToolResult.Ok("ok"));
        }
    }

    private sealed class ScriptedAiClient : IAiClient
    {
        private readonly Queue<AgentEvent[]> _turns;
        public ScriptedAiClient(params AgentEvent[][] turns) => _turns = new Queue<AgentEvent[]>(turns);
        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null,
            bool retryDiversify = false)
        {
            var events = _turns.Count > 0 ? _turns.Dequeue() : new AgentEvent[] { new TextDelta("") };
            foreach (var e in events) { await Task.CompletedTask; yield return e; }
        }
    }

    private sealed class FixedFactory : IAiClientFactory
    {
        private readonly IAiClient _client;
        public FixedFactory(IAiClient client) => _client = client;
        public IAiClient ResolveCurrent() => _client;
    }

    private sealed class AllowAllSafety : ISafetyPolicy
    {
        public bool AutoApprove => true;
        public SafetyDecision Evaluate(string toolName, JsonElement arguments) => SafetyDecision.Allow;
    }

    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct)
            => Task.FromResult(true);
    }
}
