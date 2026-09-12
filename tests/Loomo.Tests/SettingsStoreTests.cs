using System;
using System.IO;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Ai;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class SettingsStoreTests
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
                "numGpu": 99,
                "thinking": true,
                "thinkingEffort": "high",
                "maxTokens": 1234
              }
            }
            """);

        try
        {
            var settings = new LoomoSettings();
            new SettingsStore(path).Load(settings);

            // systemPrompt / baseUrl / numGpu などの旧フィールドは読み捨て、残りは正しく反映される
            // （システムプロンプトはユーザー設定ではなく AI 層の SystemPrompts が持つ）。
            Assert.Equal("phi4-mini:latest", settings.Local.Model);
            Assert.Equal(1234, settings.Local.MaxTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveLoad_保持する_LSP促しの今後表示しない拡張子()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.Lsp.DismissedPromptExtensions.Add(".java");
            new SettingsStore(path).Save(saved);

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);

            Assert.Equal([".java"], loaded.Lsp.DismissedPromptExtensions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_migrates_legacy_default_model_to_current_default()
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
            var settings = new LoomoSettings();
            new SettingsStore(path).Load(settings);

            Assert.Equal(LoomoSettings.DefaultLocalModel, settings.Local.Model);
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
            new SettingsStore(path).Save(new LoomoSettings());

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.False(json.ContainsKey("systemPrompt"));
            Assert.False(json["local"]!.AsObject().ContainsKey("baseUrl"));
            Assert.False(json["local"]!.AsObject().ContainsKey("numGpu"));
            Assert.False(json["local"]!.AsObject().ContainsKey("thinking"));
            Assert.False(json["local"]!.AsObject().ContainsKey("thinkingEffort"));
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
            var saved = new LoomoSettings();
            saved.Vim.Enabled = true;

            var store = new SettingsStore(path);
            store.Save(saved);

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True(json["vim"]!["enabled"]!.GetValue<bool>());

            var loaded = new LoomoSettings();
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
        Assert.False(new LoomoSettings().Vim.Enabled);
    }

    [Fact]
    public void Save_and_load_persists_collapse_usings_on_open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.Editor.CollapseUsingsOnOpen = true;

            var store = new SettingsStore(path);
            store.Save(saved);

            var loaded = new LoomoSettings();
            store.Load(loaded);

            Assert.True(loaded.Editor.CollapseUsingsOnOpen);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Collapse_usings_on_open_is_disabled_by_default()
    {
        Assert.False(new LoomoSettings().Editor.CollapseUsingsOnOpen);
    }

    [Fact]
    public void Save_and_load_persists_inlay_hints_setting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.Editor.ShowInlayHints = true;

            var store = new SettingsStore(path);
            store.Save(saved);

            var loaded = new LoomoSettings();
            store.Load(loaded);

            Assert.True(loaded.Editor.ShowInlayHints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inlay_hints_are_disabled_by_default()
        => Assert.False(new LoomoSettings().Editor.ShowInlayHints);

    /// <summary>
    /// 設定は <c>PersistedSettings</c> という DTO を経由して読み書きされる。新しい設定を
    /// <see cref="LoomoSettings"/> へ足しても DTO 側へ足し忘れると、<b>JSON は読み捨てられ、
    /// 保存しても消える</b>——実際それで「入力の先読みが一度も動かない」を出した。
    /// セクションごと往復することをここで固定する。
    /// </summary>
    [Fact]
    public void Inline_completion_settings_survive_a_save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.InlineCompletion.Enabled = true;
            saved.InlineCompletion.ModelPath = @"C:\models\completion\model.gguf";
            saved.InlineCompletion.PrefixLines = 40;
            saved.InlineCompletion.SuffixLines = 2;
            saved.InlineCompletion.MaxTokens = 12;
            saved.InlineCompletion.Threads = 4;
            saved.Editor.InlineSuggest = false;
            new SettingsStore(path).Save(saved);

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);

            Assert.True(loaded.InlineCompletion.Enabled);
            Assert.Equal(@"C:\models\completion\model.gguf", loaded.InlineCompletion.ModelPath);
            Assert.Equal(40, loaded.InlineCompletion.PrefixLines);
            Assert.Equal(2, loaded.InlineCompletion.SuffixLines);
            Assert.Equal(12, loaded.InlineCompletion.MaxTokens);
            Assert.Equal(4, loaded.InlineCompletion.Threads);
            Assert.False(loaded.Editor.InlineSuggest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>先読みの設定が無い古い settings.json は、既定（無効・内蔵の先読みは有効）のまま読める。</summary>
    [Fact]
    public void A_settings_file_without_the_inline_completion_section_keeps_the_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "local": { "model": "qwen3-4b-q4_k_m" } }
            """);
        try
        {
            var settings = new LoomoSettings();
            new SettingsStore(path).Load(settings);

            Assert.False(settings.InlineCompletion.Enabled);
            Assert.Null(settings.InlineCompletion.ModelPath);
            Assert.True(settings.Editor.InlineSuggest);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
