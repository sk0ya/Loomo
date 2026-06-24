using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void Tool_step_fields_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows");
        var store = new WorkflowStore(dir);

        var id = store.Save(new Workflow
        {
            Name = "ツール入り",
            Steps =
            {
                new WorkflowStep { Title = "置換", Kind = WorkflowStepKind.Transform, Prompt = "{{prev}}", Pattern = "a", Content = "b", IsRegex = true },
                new WorkflowStep { Title = "保存", Kind = WorkflowStepKind.WriteFile, Prompt = "out.txt", Content = "{{prev}}" },
            },
        });

        var loaded = store.Load(id);

        Assert.NotNull(loaded);
        Assert.Equal(WorkflowStepKind.Transform, loaded!.Steps[0].Kind);
        Assert.Equal("a", loaded.Steps[0].Pattern);
        Assert.Equal("b", loaded.Steps[0].Content);
        Assert.True(loaded.Steps[0].IsRegex);
        Assert.Equal(WorkflowStepKind.WriteFile, loaded.Steps[1].Kind);
        Assert.Equal("out.txt", loaded.Steps[1].Prompt);
        Assert.Equal("{{prev}}", loaded.Steps[1].Content);
    }

    [Fact]
    public void ListInputWorkflows_returns_only_workflows_using_input_token()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows");
        var store = new WorkflowStore(dir);

        // {{input}} を prompt で使う → 対象
        var promptId = store.Save(new Workflow
        {
            Name = "入力をprompt",
            Steps = { new WorkflowStep { Prompt = "次を翻訳: {{input}}" } },
        });
        // {{input}} を content で使う（非AIステップ）→ 対象
        var contentId = store.Save(new Workflow
        {
            Name = "入力をcontent",
            Steps = { new WorkflowStep { Kind = WorkflowStepKind.WriteFile, Prompt = "out.txt", Content = "{{input}}" } },
        });
        // {{input}} を使わない → 除外
        store.Save(new Workflow
        {
            Name = "入力なし",
            Steps = { new WorkflowStep { Prompt = "{{prev}} を要約" } },
        });

        var ids = store.ListInputWorkflows().Select(s => s.Id).ToHashSet();

        Assert.Contains(promptId, ids);
        Assert.Contains(contentId, ids);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Load_defaults_missing_kind_to_ai()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "nokind.json"), """
        {
          "id": "nokind",
          "name": "種別なし",
          "createdAt": "2026-06-01T00:00:00",
          "updatedAt": "2026-06-01T00:00:00",
          "steps": [ { "title": "要約", "prompt": "要約して" } ]
        }
        """);

        var workflow = new WorkflowStore(dir).Load("nokind");

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStepKind.Ai, Assert.Single(workflow!.Steps).Kind);
    }
}
