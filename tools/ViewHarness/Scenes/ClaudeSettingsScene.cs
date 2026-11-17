using System.IO;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;

namespace ViewHarness.Scenes;

/// <summary>設定 → 外観の下の「Claude Code」欄（<see cref="ClaudeCodeSettingsSection"/>）を、本物の部品と本物の
/// <see cref="AppearanceViewModel"/> で描く。既定の状態、2 つの項目を入れ替えた状態を撮る。
/// 設定は一時フォルダーの SettingsStore へ書く（%APPDATA% は触らない）。</summary>
public sealed class ClaudeSettingsScene : IScene
{
    private AppearanceViewModel? _appearance;

    public string Name => "claude-settings";
    public string Description => "設定の Claude Code 欄（本物の ClaudeCodeSettingsSection）：既定・項目を入れ替えた状態";
    public Size Size => new(560, 440);

    public FrameworkElement Build()
    {
        var settings = new LoomoSettings();
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "loomo-vh-claude-settings.json"));
        _appearance = new AppearanceViewModel(settings, store, new ThemeManager(), new UiFontManager());
        return new Border
        {
            Padding = new Thickness(16),
            Child = new ClaudeCodeSettingsSection { DataContext = _appearance },
        };
    }

    public IEnumerable<SceneStep> Steps(FrameworkElement root)
    {
        var vm = _appearance!;
        yield return new SceneStep("motion-on", () => vm.ClaudeContinueOnBrokenMotion = true);
        yield return new SceneStep("ide-off", () => vm.ClaudeCodeIdeIntegration = false);
    }
}
