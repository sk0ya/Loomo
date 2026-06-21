using System;
using System.IO;
using System.Text.Json;
using sk0ya.Loomo.Core.Agent;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class WorkflowStoreTests
{
    [Fact]
    public void Load_ignores_legacy_use_tools_field()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "legacy.json"), """
        {
          "id": "legacy",
          "name": "旧ワークフロー",
          "createdAt": "2026-06-01T00:00:00",
          "updatedAt": "2026-06-01T00:00:00",
          "steps": [
            { "title": "要約", "prompt": "要約して", "useTools": false }
          ]
        }
        """);

        var workflow = new WorkflowStore(dir).Load("legacy");

        Assert.NotNull(workflow);
        var step = Assert.Single(workflow!.Steps);
        Assert.Equal("要約", step.Title);
        Assert.Equal("要約して", step.Prompt);
    }

    [Fact]
    public void Save_omits_use_tools_field()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows");
        var store = new WorkflowStore(dir);

        var id = store.Save(new Workflow
        {
            Name = "新ワークフロー",
            Steps = { new WorkflowStep { Title = "要約", Prompt = "要約して" } },
        });

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, id + ".json")));
        var step = doc.RootElement.GetProperty("steps")[0];
        Assert.False(step.TryGetProperty("useTools", out _));
    }
}
