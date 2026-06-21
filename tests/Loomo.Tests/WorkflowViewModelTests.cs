using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class WorkflowViewModelTests
{
    private static WorkflowViewModel CreateSut()
    {
        var approval = new UiApprovalService();
        var orchestrator = new AgentOrchestrator(
            new FakeAiClientFactory(),
            new ToolRegistry(Enumerable.Empty<IAgentTool>()),
            approval,
            new SafetyPolicy(new SafetySettings()),
            NoopContextWindowPolicy.Instance,
            NullLogger<AgentOrchestrator>.Instance);
        var store = new WorkflowStore(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows"));

        return new WorkflowViewModel(orchestrator, approval, store, new FakeAiWarmup());
    }

    [Fact]
    public void Starts_without_default_step()
    {
        var sut = CreateSut();

        Assert.Empty(sut.Steps);
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
    public void Removing_last_step_leaves_workflow_empty()
    {
        var sut = CreateSut();
        sut.AddStepCommand.Execute(null);

        sut.RemoveStepCommand.Execute(sut.Steps[0]);

        Assert.Empty(sut.Steps);
    }
}
