using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Input;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ジェスチャモデル（KeyChord / KeySequence）・コマンドカタログ・KeybindingService の検証。</summary>
public class KeybindingModelTests
{
    // ===== KeyChord =====

    [Theory]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Ctrl+W")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Alt+F4")]
    [InlineData("F11")]
    [InlineData("Shift+H")]
    [InlineData("1")]
    public void KeyChord_round_trips(string text)
    {
        var chord = KeyChord.TryParse(text);
        Assert.NotNull(chord);
        Assert.Equal(text, chord!.Value.Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]            // 修飾子のみ
    [InlineData("Ctrl+Shift")]      // 修飾子のみ
    [InlineData("Ctrl+A+B")]        // 非修飾キーが 2 つ
    public void KeyChord_rejects_invalid(string text)
        => Assert.Null(KeyChord.TryParse(text));

    [Fact]
    public void KeyChord_parses_modifiers_and_key()
    {
        var chord = KeyChord.TryParse("Ctrl+Shift+P");
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, chord!.Value.Modifiers);
        Assert.Equal(Key.P, chord.Value.Key);
    }

    // ===== KeySequence =====

    [Theory]
    [InlineData("Ctrl+Shift+P", 1)]
    [InlineData("Ctrl+W H", 2)]
    [InlineData("Ctrl+W Shift+L", 2)]
    public void KeySequence_round_trips(string text, int count)
    {
        var seq = KeySequence.TryParse(text);
        Assert.NotNull(seq);
        Assert.Equal(count, seq!.Count);
        Assert.Equal(text, seq.Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+W H L")]      // 3 chord は不可
    [InlineData("Ctrl+W +")]        // 2 打目が不正
    public void KeySequence_rejects_invalid(string text)
        => Assert.Null(KeySequence.TryParse(text));

    [Fact]
    public void KeySequence_value_equality()
    {
        Assert.Equal(KeySequence.TryParse("Ctrl+W H"), KeySequence.TryParse("Ctrl+W H"));
        Assert.NotEqual(KeySequence.TryParse("Ctrl+W H"), KeySequence.TryParse("Ctrl+W J"));
    }

    // ===== CommandCatalog =====

    [Fact]
    public void Catalog_ids_are_unique()
    {
        var ids = CommandCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Catalog_default_bindings_parse()
    {
        foreach (var d in CommandCatalog.All.Where(c => c.DefaultBinding is not null))
            Assert.True(KeySequence.TryParse(d.DefaultBinding) is not null,
                $"{d.Id} の既定ジェスチャ '{d.DefaultBinding}' がパースできない");
    }

    [Fact]
    public void Catalog_resize_commands_enter_resize_mode()
    {
        foreach (var d in CommandCatalog.All.Where(c => c.Id.StartsWith("pane.resize.")))
            Assert.Equal(CommandCatalog.ResizeMode, d.EntersMode);
    }

    // ===== KeybindingService =====

    private static (KeybindingService Service, string Path) NewService()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-kb-{Guid.NewGuid():N}.json");
        var settings = new AiSettings();
        var store = new AiSettingsStore(path);
        return (new KeybindingService(settings, store), path);
    }

    [Fact]
    public void Service_resolves_catalog_defaults()
    {
        var (service, path) = NewService();
        try
        {
            Assert.Equal(KeySequence.TryParse("Ctrl+W H"), service.For("pane.focus.left"));
            Assert.Equal(KeySequence.TryParse("Ctrl+Shift+P"), service.For("palette.open"));
            Assert.Equal(KeySequence.TryParse("F11"), service.For("pane.fullscreen"));
            Assert.False(service.IsCustom("pane.focus.left"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Service_rebind_and_reset()
    {
        var (service, path) = NewService();
        try
        {
            var gesture = KeySequence.TryParse("Ctrl+Alt+G");
            service.Rebind("pane.focus.left", gesture);
            Assert.Equal(gesture, service.For("pane.focus.left"));
            Assert.True(service.IsCustom("pane.focus.left"));
            Assert.Equal("pane.focus.left", service.CommandAt(gesture!));

            service.Reset("pane.focus.left");
            Assert.Equal(KeySequence.TryParse("Ctrl+W H"), service.For("pane.focus.left"));
            Assert.False(service.IsCustom("pane.focus.left"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Service_rebind_to_default_clears_override()
    {
        var (service, path) = NewService();
        try
        {
            service.Rebind("pane.focus.left", KeySequence.TryParse("Ctrl+W H")); // = 既定
            Assert.False(service.IsCustom("pane.focus.left"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Service_rebind_null_makes_unassigned()
    {
        var (service, path) = NewService();
        try
        {
            service.Rebind("pane.focus.left", null);
            Assert.Null(service.For("pane.focus.left"));
            Assert.True(service.IsCustom("pane.focus.left"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Service_detects_conflicts()
    {
        var (service, path) = NewService();
        try
        {
            var gesture = KeySequence.TryParse("Ctrl+W H"); // 既定で pane.focus.left
            service.Rebind("pane.focus.right", gesture);     // 競合させる
            var rightRow = service.Rows().Single(r => r.Descriptor.Id == "pane.focus.right");
            Assert.Equal("pane.focus.left", rightRow.ConflictId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Service_persists_overrides_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-kb-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AiSettingsStore(path);
            var first = new KeybindingService(new AiSettings(), store);
            first.Rebind("pane.zoom", KeySequence.TryParse("Ctrl+Alt+Z"));

            // 別インスタンスで読み直す（ファイル経由の往復）。
            var reloaded = new AiSettings();
            store.Load(reloaded);
            var second = new KeybindingService(reloaded, store);
            Assert.Equal(KeySequence.TryParse("Ctrl+Alt+Z"), second.For("pane.zoom"));
            Assert.True(second.IsCustom("pane.zoom"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_keybindings_default_to_empty_when_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-kb-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "local": { "model": "phi4-mini" } }""");
        try
        {
            var settings = new AiSettings();
            new AiSettingsStore(path).Load(settings);
            Assert.Empty(settings.Keybindings.Overrides); // 旧設定は上書き無し＝既定割り当て
        }
        finally { File.Delete(path); }
    }
}
