using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>ウィンドウ生成前に必要な、順序依存のアプリ初期化を一か所で実行する。</summary>
internal sealed class AppBootstrapper
{
    private readonly LoomoSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ThemeManager _themeManager;
    private readonly UiFontManager _fontManager;
    private readonly IServiceProvider _services;

    public AppBootstrapper(
        LoomoSettings settings,
        SettingsStore settingsStore,
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

        // 自分のテーマ（%APPDATA%/Loomo/themes/*.json）は数ファイルの JSON なので起動時に読んでしまう。
        // 見張りも始め、保存し直すとその場で当て直す（購読は外観パネルの VM）。
        UserThemes.Reload();
        UserThemes.StartWatching();
        var userTheme = _settings.UserTheme is { } id ? UserThemes.TryGet(UserTheme.KeyPrefix + id) : null;
        _themeManager.Apply(userTheme, _settings.Theme, _settings.AccentColor);
        _fontManager.Apply(UiFontManager.Effective(_settings.Appearance.UiFontSize));
        StartupProfiler.Mark("テーマ適用完了");
    }
}
