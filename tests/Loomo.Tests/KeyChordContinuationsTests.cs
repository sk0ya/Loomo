using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>連鎖キーの続きの案内（§24.2.2）に出す一覧。押しても何も起きない（アクションが結線されていない）
/// コマンドは出さない。</summary>
public class KeyChordContinuationsTests
{
    [Fact]
    public void Lists_only_commands_with_wired_actions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-kb-{Guid.NewGuid():N}.json");
        try
        {
            var service = new KeybindingService(new LoomoSettings(), new SettingsStore(path));
            var actions = new Dictionary<string, Action>(StringComparer.Ordinal)
            {
                ["pane.focus.left"] = () => { },
                ["pane.zoom"] = () => { },
            };
            var dispatcher = new KeyboardDispatcher(service, actions);

            var next = dispatcher.ContinuationsOf(new KeyChord(ModifierKeys.Control, Key.W));

            Assert.Equal(["pane.focus.left", "pane.zoom"], next.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
        }
        finally { File.Delete(path); }
    }
}
