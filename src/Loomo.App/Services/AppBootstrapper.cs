using System.IO;
using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>ウィンドウ生成前に必要な、順序依存のアプリ初期化を一か所で実行する。</summary>
internal sealed class AppBootstrapper
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _settingsStore;
    private readonly ThemeManager _themeManager;
    private readonly UiFontManager _fontManager;
    private readonly IServiceProvider _services;

    public AppBootstrapper(
        AiSettings settings,
        AiSettingsStore settingsStore,
        ThemeManager themeManager,
        UiFontManager fontManager,
        IServiceProvider services)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _themeManager = themeManager;
        _fontManager = fontManager;
        _services = services;
    }

    public void Initialize()
    {
        _settingsStore.Load(_settings);
        StartupProfiler.Mark("設定ロード完了");

        var loomoDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo");
        Editor.Core.Lsp.LspServerRegistry.ConfigureDefault(
            Path.Combine(loomoDataDirectory, "lsp-servers.json"));
        Editor.Core.Formatting.FormatterRegistry.ConfigureDefault(
            Path.Combine(loomoDataDirectory, "formatters.json"));

        _themeManager.Apply(_settings.Theme, _settings.AccentColor);
        _fontManager.Apply(UiFontManager.Effective(_settings.Appearance.UiFontSize));
        StartupProfiler.Mark("テーマ適用完了");

        // 設定ロード後に解決し、保存済みのモデル設定を使って暖機を開始する。
        _services.GetRequiredService<LocalLlmWarmupService>();
        StartupProfiler.Mark("ウォームアップ起動完了");
    }
}
