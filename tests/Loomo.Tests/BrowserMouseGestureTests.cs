using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ブラウザのマウスジェスチャー（§24.105）。</summary>
public sealed class BrowserMouseGestureTests
{
    private static string Message(string token, string gesture, string type = BrowserMouseGesture.MessageType)
        => $$"""{"type":"{{type}}","token":"{{token}}","gesture":"{{gesture}}"}""";

    [Theory]
    [InlineData("D", "D")]
    [InlineData("dr", "DR")]
    [InlineData("DDRR", "DR")]
    [InlineData("UDU", "UDU")]
    public void 向きは大文字にして続く同じ向きを畳む(string raw, string expected)
        => Assert.Equal(expected, BrowserMouseGesture.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DX")]
    [InlineData("UDUDUDUDU")]   // 9 画は描き損じ
    public void 向きにならないものは無し(string? raw)
        => Assert.Null(BrowserMouseGesture.Normalize(raw));

    [Fact]
    public void 合言葉の合う合図だけ読む()
    {
        Assert.True(BrowserMouseGesture.TryRead(Message("t", "D"), "t", out var gesture));
        Assert.Equal("D", gesture);

        Assert.False(BrowserMouseGesture.TryRead(Message("偽物", "D"), "t", out _));
        Assert.False(BrowserMouseGesture.TryRead(Message("t", "D", type: "openExtensionPage"), "t", out _));
        Assert.False(BrowserMouseGesture.TryRead(Message("t", "DX"), "t", out _));
        Assert.False(BrowserMouseGesture.TryRead("\"文字列\"", "t", out _));
        Assert.False(BrowserMouseGesture.TryRead("{壊れた", "t", out _));
        Assert.False(BrowserMouseGesture.TryRead("""{"type":"loomo.gesture","token":"t","gesture":3}""", "t", out _));
    }

    [Fact]
    public void 仕込みには起動ごとの合言葉が入る()
    {
        Assert.Contains(BrowserMouseGesture.Token, BrowserMouseGesture.Script);
        Assert.DoesNotContain("__LOOMO_GESTURE_TOKEN__", BrowserMouseGesture.Script);
    }

    [Fact]
    public void 既定は下でリンクのコピー()
    {
        var bindings = new MouseGestureSettings().Bindings;

        Assert.Equal("browser.copyLink", BrowserMouseGesture.Resolve(bindings, "D"));
        Assert.Equal("browser.back", BrowserMouseGesture.Resolve(bindings, "L"));
        Assert.Equal("browser.forward", BrowserMouseGesture.Resolve(bindings, "R"));
        Assert.Equal("browser.reload", BrowserMouseGesture.Resolve(bindings, "UD"));
        Assert.Equal("browser.closeTab", BrowserMouseGesture.Resolve(bindings, "DR"));
        Assert.Null(BrowserMouseGesture.Resolve(bindings, "U"));
    }

    [Fact]
    public void 既定のジェスチャーのコマンドはどれもカタログにある()
    {
        var ids = CommandCatalog.All.Select(c => c.Id).ToHashSet();
        Assert.All(MouseGestureSettings.Defaults().Values, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void 手で書いた小文字の鍵も引ける()
        => Assert.Equal("browser.copyUrl",
            BrowserMouseGesture.Resolve(new Dictionary<string, string> { ["dl"] = "browser.copyUrl" }, "DL"));

    [Fact]
    public void 矢印で見せる()
        => Assert.Equal("↓→↑←", BrowserMouseGesture.Arrows("DRUL"));

    [Theory]
    [InlineData("↓→", "DR")]
    [InlineData("↓, →", "DR")]
    [InlineData("d r", "DR")]
    [InlineData("U-D", "UD")]
    public void 設定の入力は矢印でも文字でも(string text, string expected)
        => Assert.Equal(expected, MouseGestureSettingsViewModel.ParseGesture(text));

    [Fact]
    public void 設定画面で足した割り当てと外した既定が保存される()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LoomoSettings();
            var vm = new MouseGestureSettingsViewModel(settings, new SettingsStore(path));
            vm.NewGesture = "↑";
            vm.NewCommand = vm.Commands.Single(c => c.Id == "browser.copyUrl");
            vm.AddCommand.Execute(null);
            vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Key == "DR"));
            vm.Enabled = false;

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);

            Assert.False(loaded.MouseGestures.Enabled);
            Assert.Equal("browser.copyUrl", loaded.MouseGestures.Bindings["U"]);
            Assert.Equal("browser.copyLink", loaded.MouseGestures.Bindings["D"]);
            Assert.False(loaded.MouseGestures.Bindings.ContainsKey("DR"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 行のコマンドを選び替えると保存される()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LoomoSettings();
            var vm = new MouseGestureSettingsViewModel(settings, new SettingsStore(path));
            var row = vm.Rows.Single(r => r.Key == "D");
            row.Command = row.Commands.Single(c => c.Id == "browser.copyUrl");

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);
            Assert.Equal("browser.copyUrl", loaded.MouseGestures.Bindings["D"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ジェスチャーの設定が無い旧設定は既定のまま()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);

            Assert.True(loaded.MouseGestures.Enabled);
            Assert.Equal("browser.copyLink", loaded.MouseGestures.Bindings["D"]);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("alert(1)", "alert(1)")]
    [InlineData("  javascript:alert(1)  ", "alert(1)")]
    [InlineData("JavaScript:(function(){var%20a=1;})();", "(function(){var a=1;})();")]
    [InlineData("javascript:alert('100%')", "alert('100%')")]   // 戻せない % はそのまま
    public void ブックマークレットはそのまま貼れる(string script, string expected)
        => Assert.Equal(expected, BrowserUserScript.Prepare(script));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("javascript:")]
    public void 走らせるものが無ければ無し(string? script)
        => Assert.Null(BrowserUserScript.Prepare(script));

    [Fact]
    public void 足したスクリプトはジェスチャーとキーの両方に出て保存される()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LoomoSettings();
            var store = new SettingsStore(path);
            var keys = new KeybindingService(settings, store);
            var changed = 0;
            keys.Changed += () => changed++;
            var vm = new MouseGestureSettingsViewModel(settings, store, keys);

            vm.NewScriptTitle = "リンクを HTML で";
            vm.NewScriptBody = "javascript:alert(document.title)";
            vm.NewScriptGesture = "↑";
            vm.AddScriptCommand.Execute(null);

            var id = Assert.Single(settings.BrowserScripts).Id;
            Assert.StartsWith(BrowserScriptCommand.IdPrefix, id);
            Assert.Equal(id, settings.MouseGestures.Bindings["U"]);
            Assert.Contains(vm.Commands, c => c.Id == id && c.Title == "スクリプト：リンクを HTML で");
            Assert.Contains(keys.Rows(), r => r.Descriptor.Id == id && r.Descriptor.Category == "ブラウザ");
            Assert.True(changed > 0);

            keys.Rebind(id, KeySequence.TryParse("Ctrl+Alt+M"));
            Assert.NotNull(keys.For(id));

            var loaded = new LoomoSettings();
            new SettingsStore(path).Load(loaded);
            var saved = Assert.Single(loaded.BrowserScripts);
            Assert.Equal(("リンクを HTML で", "javascript:alert(document.title)"), (saved.Title, saved.Script));
            Assert.Equal(id, loaded.MouseGestures.Bindings["U"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 名前を変えても割り当ては外れない()
    {
        var settings = new LoomoSettings();
        settings.BrowserScripts.Add(new BrowserScriptCommand { Id = "browser.script.abc", Title = "旧", Script = "1" });
        settings.MouseGestures.Bindings["U"] = "browser.script.abc";
        var vm = new MouseGestureSettingsViewModel(settings, null);

        vm.Scripts.Single().Title = "新";

        Assert.Equal("新", settings.BrowserScripts.Single().Title);
        Assert.Equal("スクリプト：新", vm.Rows.Single(r => r.Key == "U").Command!.Title);
    }

    [Fact]
    public void スクリプトを外すとそれを指す割り当ても外れる()
    {
        var settings = new LoomoSettings();
        settings.BrowserScripts.Add(new BrowserScriptCommand { Id = "browser.script.abc", Title = "t", Script = "1" });
        settings.MouseGestures.Bindings["U"] = "browser.script.abc";
        settings.Keybindings.Overrides["browser.script.abc"] = "Ctrl+Alt+M";
        var vm = new MouseGestureSettingsViewModel(settings, null);

        vm.RemoveScriptCommand.Execute(vm.Scripts.Single());

        Assert.Empty(settings.BrowserScripts);
        Assert.False(settings.MouseGestures.Bindings.ContainsKey("U"));
        Assert.False(settings.Keybindings.Overrides.ContainsKey("browser.script.abc"));
        Assert.DoesNotContain(vm.Commands, c => c.Id == "browser.script.abc");
    }

    [Fact]
    public void キーの実行表は設定から足したコマンドも引く()
    {
        var builtIn = new Dictionary<string, Action> { ["a"] = () => { } };
        var dynamic = new List<string> { "browser.script.x" };
        var ran = false;
        var table = new CommandActionTable(builtIn,
            id => dynamic.Contains(id) ? () => ran = true : null, () => dynamic);

        Assert.True(table.ContainsKey("a"));
        Assert.True(table.TryGetValue("browser.script.x", out var action));
        action();
        Assert.True(ran);
        Assert.False(table.ContainsKey("browser.script.y"));
        Assert.Equal(new[] { "a", "browser.script.x" }, table.Keys);

        dynamic.Clear();
        Assert.False(table.ContainsKey("browser.script.x"));
    }
}
