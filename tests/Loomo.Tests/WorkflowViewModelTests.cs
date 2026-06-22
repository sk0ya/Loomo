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
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class WorkflowViewModelTests
{
    private static WorkflowViewModel CreateSut()
        => CreateSut(new FakeAiClientFactory(), new ToolRegistry(Enumerable.Empty<IAgentTool>()));

    private static WorkflowViewModel CreateSut(IAiClientFactory aiFactory, ToolRegistry tools)
        => CreateSut(aiFactory, tools, new FakeAiWarmup());

    private static WorkflowViewModel CreateSut(IAiClientFactory aiFactory, ToolRegistry tools, IAiWarmup warmup)
    {
        var approval = new UiApprovalService();
        var orchestrator = new AgentOrchestrator(
            aiFactory,
            tools,
            approval,
            new SafetyPolicy(new SafetySettings()),
            NoopContextWindowPolicy.Instance,
            NullLogger<AgentOrchestrator>.Instance);
        var store = new WorkflowStore(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows"));

        return new WorkflowViewModel(orchestrator, approval, store, warmup, new AiSettings());
    }

    [Fact]
    public void Starts_without_default_step()
    {
        var sut = CreateSut();

        Assert.Empty(sut.Steps);
    }

    [Fact]
    public async Task Workflow_step_passes_registered_tools_to_keep_warmup_prefix()
    {
        var ai = new ToolsRecordingAiClient(new TextDelta("ok"));
        var tools = new ToolRegistry(new IAgentTool[] { new DummyTool() });
        var sut = CreateSut(new FixedFactory(ai), tools);
        sut.Steps.Add(new WorkflowStepViewModel
        {
            Prompt = "要約して",
        });

        await sut.RunCommand.ExecuteAsync(null);

        Assert.NotNull(ai.LastTools);
        var tool = Assert.Single(ai.LastTools!);
        Assert.Equal("dummy", tool.Name);
    }

    [Fact]
    public async Task Workflow_activity_uses_chat_progress_style_without_step_headers()
    {
        var ai = new ToolsRecordingAiClient(
            new RawTextDelta("ok"),
            new TextDelta("ok"),
            new AiUsageReported(10, 2, 0, 100, 20, 120));
        var sut = CreateSut(new FixedFactory(ai), new ToolRegistry(Enumerable.Empty<IAgentTool>()));
        sut.Steps.Add(new WorkflowStepViewModel { Prompt = "要約して" });

        await sut.RunCommand.ExecuteAsync(null);

        Assert.Contains(sut.Activity.Steps, s => s.Message.StartsWith("実行構成:", StringComparison.Ordinal));
        Assert.Contains(sut.Activity.Steps, s => s.Message == "AIに送信しました。応答を待っています。");
        Assert.Contains(sut.Activity.Steps, s => s.Message == "回答本文の生成を開始しました。");
        Assert.DoesNotContain(sut.Activity.Steps, s => s.Message.StartsWith("ステップ", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_step_creates_step_one()
    {
        var sut = CreateSut();

        sut.AddStepCommand.Execute(null);

        var step = Assert.Single(sut.Steps);
        Assert.Equal(1, step.Index);
    }

    [Fact]
    public void Run_command_is_disabled_while_global_warmup_is_running()
    {
        var warmup = new ControllableWarmup { IsWarmingUpValue = true };
        var sut = CreateSut(
            new FakeAiClientFactory(),
            new ToolRegistry(Enumerable.Empty<IAgentTool>()),
            warmup);
        sut.Steps.Add(new WorkflowStepViewModel { Prompt = "要約して" });

        Assert.False(sut.RunCommand.CanExecute(null));
        Assert.True(sut.IsWarmingUp);
        Assert.True(sut.IsProgressVisible);
        Assert.Contains("ウォームアップ中", sut.RunStatus);

        var canExecuteChanged = 0;
        sut.RunCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        warmup.IsWarmingUpValue = false;
        warmup.RaiseStateChanged();

        Assert.True(sut.RunCommand.CanExecute(null));
        Assert.False(sut.IsWarmingUp);
        Assert.False(sut.IsProgressVisible);
        Assert.True(canExecuteChanged > 0);
    }

    [Fact]
    public void Step_warns_when_later_prompt_does_not_reference_previous_output()
    {
        var first = new WorkflowStepViewModel { Prompt = "調べて" };
        var second = new WorkflowStepViewModel { Prompt = "要約して" };

        first.Index = 1;
        second.Index = 2;

        Assert.Equal("", first.PromptNotice);
        Assert.Contains("自動では渡りません", second.PromptNotice);
    }

    [Fact]
    public void Append_previous_adds_prev_placeholder()
    {
        var step = new WorkflowStepViewModel { Index = 2, Prompt = "要約して" };

        step.AppendPreviousCommand.Execute(null);

        Assert.Contains("{{prev}}", step.Prompt);
        Assert.Equal("", step.PromptNotice);
    }

    [Fact]
    public void Editing_step_auto_saves_and_updates_workflow_list()
    {
        var sut = CreateSut();
        sut.AddStepCommand.Execute(null);

        sut.Steps[0].Prompt = "要約して";

        Assert.False(sut.HasUnsavedChanges);
        Assert.Equal("自動保存済み", sut.SaveStatus);
        Assert.Equal("1 ステップを実行", sut.WorkflowSummaryText);
        Assert.Single(sut.SavedWorkflows);
    }

    [Fact]
    public void Removing_last_step_leaves_workflow_empty()
    {
        var sut = CreateSut();
        sut.AddStepCommand.Execute(null);

        sut.RemoveStepCommand.Execute(sut.Steps[0]);

        Assert.Empty(sut.Steps);
    }

    private sealed class DummyTool : IAgentTool
    {
        public string Name => "dummy";
        public ToolDefinition Definition => new(Name, "dummy", new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("ran"));
    }

    private sealed class ToolsRecordingAiClient : IAiClient
    {
        private readonly AgentEvent[] _events;
        public IReadOnlyList<ToolDefinition>? LastTools { get; private set; }

        public ToolsRecordingAiClient(params AgentEvent[] events) => _events = events;

        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null,
            bool retryDiversify = false)
        {
            LastTools = tools;
            foreach (var e in _events)
            {
                await Task.CompletedTask;
                yield return e;
            }
        }
    }

    private sealed class FixedFactory : IAiClientFactory
    {
        private readonly IAiClient _client;
        public FixedFactory(IAiClient client) => _client = client;
        public IAiClient Resolve(AiProvider provider) => _client;
        public IAiClient ResolveCurrent() => _client;
    }

    private sealed class ControllableWarmup : IAiWarmup
    {
        public bool IsWarmingUpValue { get; set; }
        public bool IsWarmingUp => IsWarmingUpValue;
        public bool IsReady => !IsWarmingUpValue;
        public DateTimeOffset? WarmupStartedAt => IsWarmingUpValue ? DateTimeOffset.Now : null;
        public string CurrentStatus => "";
        public string StatusDetails => "";
        public IReadOnlyList<WarmupStageTiming> StageTimings => Array.Empty<WarmupStageTiming>();
        public TimeSpan? TotalDuration => null;
        public event Action? StateChanged;
        public void RequestWarmup() { }
        public Task EnsureWarmAsync(CancellationToken ct) => Task.CompletedTask;
        public void RaiseStateChanged() => StateChanged?.Invoke();
    }
}
