using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class SettingsStoreTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SaveLoad_ActivityBarの選択と開閉と寸法をファイルから復元する(bool primary, bool secondary)
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.ActivityBar.PrimarySelection = "todo";
            saved.ActivityBar.SecondarySelection = "git";
            saved.ActivityBar.PrimaryVisible = primary;
            saved.ActivityBar.SecondaryVisible = secondary;
            saved.ActivityBar.SidebarWidth = 345;
            saved.ActivityBar.SecondaryHeight = 321;
            saved.ActivityBar.Primary = ["explorer", "todo"];
            saved.ActivityBar.Secondary = ["tabs", "git", "solution"];
            new SettingsStore(path).Save(saved);

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);

            Assert.Equal("todo", loaded.ActivityBar.PrimarySelection);
            Assert.Equal("git", loaded.ActivityBar.SecondarySelection);
            Assert.Equal(primary, loaded.ActivityBar.PrimaryVisible);
            Assert.Equal(secondary, loaded.ActivityBar.SecondaryVisible);
            Assert.Equal(345, loaded.ActivityBar.SidebarWidth);
            Assert.Equal(321, loaded.ActivityBar.SecondaryHeight);
            Assert.Equal(saved.ActivityBar.Primary, loaded.ActivityBar.Primary);
            Assert.Equal(saved.ActivityBar.Secondary, loaded.ActivityBar.Secondary);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_旧ActivityBar設定は従来の選択と寸法を使う()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"activityBar":{"primary":["explorer"],"secondary":["tabs"]}}""");
            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);
            Assert.Equal("explorer", loaded.ActivityBar.PrimarySelection);
            Assert.Equal("tabs", loaded.ActivityBar.SecondarySelection);
            Assert.True(loaded.ActivityBar.PrimaryVisible);
            Assert.True(loaded.ActivityBar.SecondaryVisible);
            Assert.Equal(220, loaded.ActivityBar.SidebarWidth);
            Assert.Equal(200, loaded.ActivityBar.SecondaryHeight);
        }
        finally { File.Delete(path); }
    }

    /// <summary>撤去した AI ペインの設定（local・safety・observability・inlineCompletion・warmupEnabled）が
    /// 残った古い settings.json も、そこを読み捨てて残りを読める。</summary>
    [Fact]
    public void Load_ignores_retired_ai_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "systemPrompt": "古い run_command プロンプト",
              "warmupEnabled": false,
              "local": { "model": "phi4-mini:latest", "maxTokens": 1234 },
              "safety": { "autoApprove": true },
              "observability": { "enableTracing": false },
              "inlineCompletion": { "enabled": true, "modelPath": "C:\\m.gguf" },
              "trailVisible": false
            }
            """);

        try
        {
            var settings = new LoomoSettings();
            new SettingsStore(path).Load(settings);

            Assert.False(settings.TrailVisible);
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
    public void Save_does_not_persist_retired_ai_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            new SettingsStore(path).Save(new LoomoSettings());

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var key in new[] { "systemPrompt", "local", "safety", "observability", "inlineCompletion", "warmupEnabled" })
                Assert.False(json.ContainsKey(key), key);
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

    [Fact]
    public void Debug_inline_values_are_enabled_by_default_and_persist_when_turned_off()
    {
        Assert.True(new LoomoSettings().Editor.ShowDebugInlineValues);

        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.Editor.ShowDebugInlineValues = false;
            var store = new SettingsStore(path);
            store.Save(saved);

            var loaded = new LoomoSettings();
            store.Load(loaded);
            Assert.False(loaded.Editor.ShowDebugInlineValues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 設定は <c>PersistedSettings</c> という DTO を経由して読み書きされる。新しい設定を
    /// <see cref="LoomoSettings"/> へ足しても DTO 側へ足し忘れると、<b>JSON は読み捨てられ、
    /// 保存しても消える</b>——実際それで「入力の先読みが一度も動かない」を出した。
    /// セクションごと往復することをここで固定する。
    /// </summary>
    [Fact]
    public void Inline_suggest_setting_survives_a_save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.Editor.InlineSuggest = false;
            new SettingsStore(path).Save(saved);

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);

            Assert.False(loaded.Editor.InlineSuggest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>連動編集（タグ名の開き/閉じを同時に書き換える）の切り替えは保存をまたいで残り、
    /// 項目の無い古い settings.json では既定の有効のまま読める。</summary>
    [Fact]
    public void Linked_editing_setting_round_trips_and_defaults_to_on()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new LoomoSettings();
            saved.Editor.LinkedEditing = false;
            new SettingsStore(path).Save(saved);
            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);
            Assert.False(loaded.Editor.LinkedEditing);

            File.WriteAllText(path, "{ \"editor\": { \"inlineSuggest\": true } }");
            var legacy = new LoomoSettings();
            new SettingsStore(path).Load(legacy);
            Assert.True(legacy.Editor.LinkedEditing);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
