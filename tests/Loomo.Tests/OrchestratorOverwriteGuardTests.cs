using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 冗長な破壊的上書きガード（<see cref="AgentOrchestrator"/>）の決定論的挙動。
/// 小モデルの主要失敗モード「正しい編集の直後に同じファイルを write_file で全文上書きして本文を破壊」を
/// ループ側で確実に止める。edit_file の対象限定変更・別ファイルへの書込はブロックしない。
/// </summary>
public class OrchestratorOverwriteGuardTests
{
    [Fact]
    public async Task Full_overwrite_of_a_file_edited_earlier_this_turn_is_blocked()
    {
        var files = new Dictionary<string, string> { ["/ws/README.md"] = "# Title\nVersion: 1.2.3\n" };
        var executed = new List<string>();
        var tools = new ToolRegistry(new IAgentTool[]
        {
            new FakeFileTool("edit_file", fullyOverwrites: false, files, executed),
            new FakeFileTool("write_file", fullyOverwrites: true, files, executed),
        });

        // 1ターン目：正しい編集（1.2.3→2.0.0）の直後に、同じファイルを "2.0.0" で全文上書きしようとする。
        var ai = new ScriptedAiClient(
            new AgentEvent[]
            {
                new ToolUseRequested(new ToolUse("e1", "edit_file",
                    "{\"path\":\"/ws/README.md\",\"old_string\":\"1.2.3\",\"new_string\":\"2.0.0\"}")),
                new ToolUseRequested(new ToolUse("w1", "write_file",
                    "{\"path\":\"/ws/README.md\",\"content\":\"2.0.0\"}")),
            },
            new AgentEvent[] { new TextDelta("バージョンを更新しました。") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunTurnAsync(conv, "READMEのバージョンを2.0.0に")) events.Add(e);

        // edit_file は実行されたが、後続の write_file は実行されていない（ブロックされた）。
        Assert.Equal(new[] { "edit_file" }, executed);

        // ファイル本文は編集後のまま：見出しが保たれ、2.0.0 になり、1.2.3 は消えている（全文上書きで破壊されていない）。
        var content = files["/ws/README.md"];
        Assert.Contains("# Title", content);
        Assert.Contains("2.0.0", content);
        Assert.DoesNotContain("1.2.3", content);

        // write_file の結果はエラーとして差し戻されている（モデルへフィードバックされる）。
        var writeResult = events.OfType<ToolExecutionCompleted>().Single(e => e.ToolUse.Name == "write_file");
        Assert.True(writeResult.Result.IsError);
        Assert.Contains("全文上書き", writeResult.Result.Content);
    }

    [Fact]
    public async Task Relative_then_absolute_path_to_same_file_is_still_blocked()
    {
        // 小モデルは同じファイルを相対／絶対で混在指定する。canonical 解決（ここでは GetFullPath 相当）で
        // 同一性を取りこぼさないことを固定する。
        var files = new Dictionary<string, string> { ["/ws/app.py"] = "def add(a,b): return a+b\n" };
        var executed = new List<string>();
        var tools = new ToolRegistry(new IAgentTool[]
        {
            new FakeFileTool("edit_file", fullyOverwrites: false, files, executed, root: "/ws"),
            new FakeFileTool("write_file", fullyOverwrites: true, files, executed, root: "/ws"),
        });

        var ai = new ScriptedAiClient(
            new AgentEvent[]
            {
                new ToolUseRequested(new ToolUse("e1", "edit_file",
                    "{\"path\":\"app.py\",\"old_string\":\"a+b\",\"new_string\":\"a + b\"}")),
                new ToolUseRequested(new ToolUse("w1", "write_file",
                    "{\"path\":\"/ws/app.py\",\"content\":\"x\"}")),
            },
            new AgentEvent[] { new TextDelta("修正しました。") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        await foreach (var _ in orch.RunTurnAsync(conv, "バグ修正")) { }

        Assert.Equal(new[] { "edit_file" }, executed);   // 絶対パスの write も同一ファイルとしてブロック
        // 全文上書き("x")がブロックされ、編集後の本文がそのまま保たれていること（空アサーションにしない）。
        Assert.Equal("def add(a,b): return a + b\n", files["/ws/app.py"]);
    }

    [Fact]
    public async Task Rewriting_the_same_file_with_write_file_is_allowed()
    {
        // create-then-rewrite を1ターンで行う正当系：write_file→write_file（同一パス）は対象限定編集の
        // 破壊ではないので両方実行される（過剰ブロック=レビュー#1 の回帰防止）。
        var files = new Dictionary<string, string>();
        var executed = new List<string>();
        var tools = new ToolRegistry(new IAgentTool[]
        {
            new FakeFileTool("write_file", fullyOverwrites: true, files, executed),
        });

        var ai = new ScriptedAiClient(
            new AgentEvent[]
            {
                new ToolUseRequested(new ToolUse("w1", "write_file", "{\"path\":\"/ws/a.txt\",\"content\":\"first\"}")),
                new ToolUseRequested(new ToolUse("w2", "write_file", "{\"path\":\"/ws/a.txt\",\"content\":\"second\"}")),
            },
            new AgentEvent[] { new TextDelta("書き直しました。") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        await foreach (var _ in orch.RunTurnAsync(conv, "書いて書き直して")) { }

        // 両方実行され、最終内容は2回目の全文。
        Assert.Equal(new[] { "write_file", "write_file" }, executed);
        Assert.Equal("second", files["/ws/a.txt"]);
    }

    [Fact]
    public async Task Writing_two_different_files_is_not_blocked()
    {
        var files = new Dictionary<string, string>();
        var executed = new List<string>();
        var tools = new ToolRegistry(new IAgentTool[]
        {
            new FakeFileTool("write_file", fullyOverwrites: true, files, executed),
        });

        var ai = new ScriptedAiClient(
            new AgentEvent[]
            {
                new ToolUseRequested(new ToolUse("w1", "write_file", "{\"path\":\"/ws/a.txt\",\"content\":\"A\"}")),
                new ToolUseRequested(new ToolUse("w2", "write_file", "{\"path\":\"/ws/b.txt\",\"content\":\"B\"}")),
            },
            new AgentEvent[] { new TextDelta("2ファイル作成しました。") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        await foreach (var _ in orch.RunTurnAsync(conv, "2ファイル作って")) { }

        // 別パスなので両方実行される。
        Assert.Equal(new[] { "write_file", "write_file" }, executed);
        Assert.Equal("A", files["/ws/a.txt"]);
        Assert.Equal("B", files["/ws/b.txt"]);
    }

    /// <summary>インメモリ辞書を書き換えるだけのファイル変更ツール（write=全文上書き / edit=部分置換）。</summary>
    private sealed class FakeFileTool : IAgentTool, IFileMutationTool
    {
        private readonly Dictionary<string, string> _files;
        private readonly List<string> _executed;
        private readonly string? _root;

        public FakeFileTool(string name, bool fullyOverwrites, Dictionary<string, string> files,
            List<string> executed, string? root = null)
        {
            Name = name;
            FullyOverwritesTarget = fullyOverwrites;
            _files = files;
            _executed = executed;
            _root = root;
        }

        public string Name { get; }
        public bool FullyOverwritesTarget { get; }
        public ToolDefinition Definition => new(Name, "fake file tool", new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;

        // 相対/絶対を canonical キーへ寄せる（root 指定時は root 配下へ結合）。実ツールの ResolvePath 相当。
        public string? ResolveTargetPath(JsonElement args)
        {
            if (!args.TryGetProperty("path", out var p)) return null;
            var path = p.GetString();
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (_root is not null && !path.StartsWith('/')) return _root.TrimEnd('/') + "/" + path;
            return path;
        }

        public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            var key = ResolveTargetPath(args)!;
            if (FullyOverwritesTarget)
            {
                _files[key] = args.GetProperty("content").GetString() ?? "";
            }
            else
            {
                var oldStr = args.GetProperty("old_string").GetString() ?? "";
                var newStr = args.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "";
                _files[key] = _files.TryGetValue(key, out var cur)
                    ? cur.Replace(oldStr, newStr)
                    : newStr;
            }
            _executed.Add(Name);
            return Task.FromResult(ToolResult.Ok($"{Name} ok: {key}"));
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
