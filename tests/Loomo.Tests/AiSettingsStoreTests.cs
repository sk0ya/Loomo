using System;
using System.IO;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Ai;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class AiSettingsStoreTests
{
    [Fact]
    public void Load_ignores_legacy_persisted_system_prompt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "systemPrompt": "古い run_command プロンプト",
              "local": {
                "model": "phi4-mini:latest",
                "baseUrl": "http://localhost:11434",
                "maxTokens": 1234
              }
            }
            """);

        try
        {
            var settings = new AiSettings();
            new AiSettingsStore(path).Load(settings);

            Assert.Equal(AiSettings.DefaultSystemPrompt, settings.SystemPrompt);
            Assert.Equal("phi4-mini:latest", settings.Local.Model);
            Assert.Equal(1234, settings.Local.MaxTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_does_not_persist_system_prompt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AiSettingsStore(path).Save(new AiSettings());

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.False(json.ContainsKey("systemPrompt"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
