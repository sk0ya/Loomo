namespace sk0ya.Loomo.Core.Models;

/// <summary>UIのカラーテーマ。各値は Themes/Palette.&lt;name&gt;.xaml に対応する。</summary>
public enum AppTheme
{
    // 暗色系
    Dark,
    SolarizedDark,
    Nord,
    Dracula,
    TokyoNight,
    OneDark,
    Monokai,
    GruvboxDark,
    CatppuccinMocha,
    Kanagawa,
    RosePine,
    EverforestDark,
    NightOwl,
    AyuDark,
    HighContrast,
    // 明色系（IsLight() に必ず追加すること）
    Light,
    SolarizedLight,
    CatppuccinLatte,
    RosePineDawn,
    GruvboxLight,
    OneLight
}

/// <summary><see cref="AppTheme"/> の補助。</summary>
public static class AppThemeExtensions
{
    /// <summary>明色（背景が明るい）テーマかどうか。テーマ切替に追随しない外部コントロール
    /// （TSV グリッド等）へ明暗どちらの配色を渡すかの判定に使う。</summary>
    public static bool IsLight(this AppTheme theme) =>
        theme is AppTheme.Light or AppTheme.SolarizedLight or AppTheme.CatppuccinLatte
            or AppTheme.RosePineDawn or AppTheme.GruvboxLight or AppTheme.OneLight;
}
