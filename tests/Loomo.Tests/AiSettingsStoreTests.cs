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
    public void Load_migrates_legacy_default_model_to_phi4_mini()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "local": {
                "model": "llama3.1",
                "baseUrl": "http://localhost:11434"
              }
            }
            """);

        try
        {
            var settings = new AiSettings();
            new AiSettingsStore(path).Load(settings);

            Assert.Equal(AiSettings.DefaultLocalModel, settings.Local.Model);
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
            Assert.False(json["local"]!.AsObject().ContainsKey("baseUrl"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_and_load_persists_vim_enabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new AiSettings();
            saved.Vim.Enabled = true;

            var store = new AiSettingsStore(path);
            store.Save(saved);

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True(json["vim"]!["enabled"]!.GetValue<bool>());

            var loaded = new AiSettings();
            store.Load(loaded);

            Assert.True(loaded.Vim.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Vim_is_disabled_by_default()
    {
        Assert.False(new AiSettings().Vim.Enabled);
    }
}
