using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Flow.Services;
using Flow.Views.Controls;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Flow の .flow ドキュメントを、EditorSupport ペイン内の埋め込みタイムラインとして表示する。
/// レンダリングは Flow.Editor の WPF コントロールに委譲し、Loomo はファイルパスと表示ライフサイクルだけを扱う。
/// </summary>
public sealed class FlowEditorSupport : IEditorSupportVisualProvider
{
    private static readonly string[] Extensions = [".flow"];
    private readonly LoomoSettings _settings;

    public FlowEditorSupport(LoomoSettings? settings = null)
    {
        _settings = settings ?? new LoomoSettings();
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // .flow は JSON 本文を表示するのではなく、Flow がファイルを読み込んで描画する。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"Flow: {Path.GetFileName(filePath)}";

    public IEditorSupportVisual CreateVisual() => new FlowVisual(_settings);
}

/// <summary>EditorSupport の1表示面に対応する Flow ワークスペース。</summary>
public sealed class FlowVisual : IEditorSupportVisual, IEditorSupportSettingsVisual
{
    private readonly System.Windows.Controls.Grid _host = new();
    private readonly LoomoSettings _settings;
    private FlowWorkspaceControl? _view;
    private string? _lastPath;
    private bool _lastPathWasValid;

    public FlowVisual(LoomoSettings? settings = null)
    {
        _settings = settings ?? new LoomoSettings();
    }

    public FrameworkElement View => _host;

    // FlowWorkspaceControl はエディタ本文へ書き戻さず、.flow ファイルへ保存する。
    public event EventHandler<EditorSupportContentEdited>? ContentEdited
    {
        add { }
        remove { }
    }

    public void OpenSettings() => _view?.OpenSettings();

    public async Task<Action> PrepareAsync(string filePath, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedPath = Path.GetFullPath(filePath);
        var loadResult = await Task.Run(() => TryValidate(normalizedPath), ct);
        ct.ThrowIfCancellationRequested();

        return () =>
        {
            if (string.Equals(_lastPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
                && _lastPathWasValid == loadResult.IsValid)
                return;

            _host.Children.Clear();
            _view = null;

            if (loadResult.IsValid)
            {
                _view = new FlowWorkspaceControl(normalizedPath);
                ApplyHostTheme(_view);
                _host.Children.Add(_view);
            }
            else
            {
                _host.Children.Add(CreateNotice(normalizedPath, loadResult.ErrorMessage));
            }

            _lastPath = normalizedPath;
            _lastPathWasValid = loadResult.IsValid;
        };
    }

    public void Dispose()
    {
        _host.Children.Clear();
        _view = null;
        _lastPath = null;
        _lastPathWasValid = false;
    }

    private static FlowLoadResult TryValidate(string filePath)
    {
        try
        {
            _ = new FlowProjectService().Load(filePath);
            return new FlowLoadResult(true, null);
        }
        catch (Exception ex)
        {
            return new FlowLoadResult(false, ex.Message);
        }
    }

    private static FrameworkElement CreateNotice(string filePath, string? errorMessage)
    {
        var fileName = Path.GetFileName(filePath);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Flowプロジェクトを表示できません",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{fileName} は空、または有効なFlow JSONではありません。",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            panel.Children.Add(new TextBlock
            {
                Text = errorMessage,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Child = panel,
        };
    }

    private void ApplyHostTheme(FlowWorkspaceControl view)
    {
        var themeKey = _settings.Theme.IsLight()
            ? Flow.Services.ThemeService.LightThemeKey
            : Flow.Services.ThemeService.DarkThemeKey;

        Flow.Services.ThemeService.ApplyTheme(themeKey, ResolveAccentColor());
        var palette = Flow.Services.ThemeService.CurrentPalette;

        // FlowWorkspaceControl intentionally owns fallback resources so it can run standalone.
        // When embedded, replace those local fallbacks with the host palette as well; otherwise
        // the timeline and the toolbar/inspector can end up using different themes.
        var resources = view.Resources;
        resources["AppWindowBackgroundBrush"] = palette.WindowBackground;
        resources["AppSurfaceBrush"] = palette.Surface;
        resources["AppSurfaceAltBrush"] = palette.SurfaceAlt;
        resources["AppSurfaceMutedBrush"] = palette.SurfaceMuted;
        resources["AppBorderBrush"] = palette.Border;
        resources["AppBorderSoftBrush"] = palette.BorderSoft;
        resources["AppBorderStrongBrush"] = palette.BorderStrong;
        resources["AppTextPrimaryBrush"] = palette.TextPrimary;
        resources["AppTextSecondaryBrush"] = palette.TextSecondary;
        resources["AppTextMutedBrush"] = palette.TextMuted;
        resources["AppAccentBrush"] = palette.Accent;
        resources["AppAccentStrongBrush"] = palette.AccentStrong;
        resources["AppAccentOutlineBrush"] = palette.AccentOutline;
        resources["AppAccentSubtleBrush"] = palette.AccentSubtle;
        resources["AppAccentSubtleStrongBrush"] = palette.AccentSubtleStrong;
        resources["AppAccentTextBrush"] = palette.AccentText;
        resources["AppInfoBrush"] = palette.Info;
        resources["AppInfoSurfaceBrush"] = palette.InfoSurface;
        resources["AppSuccessBrush"] = palette.Success;
        resources["AppSuccessSurfaceBrush"] = palette.SuccessSurface;
        resources["AppDangerBrush"] = palette.Danger;
        resources["AppWarningSurfaceBrush"] = palette.WarningSurface;
        resources["AppWarningBorderBrush"] = palette.WarningBorder;
        resources["AppWarningTextBrush"] = palette.WarningText;
    }

    private string ResolveAccentColor()
    {
        if (ThemeManager.IsValidColor(_settings.AccentColor))
            return _settings.AccentColor!;

        if (Application.Current?.TryFindResource("Accent") is SolidColorBrush brush)
            return brush.Color.ToString();

        return Flow.Services.ThemeService.DefaultAccentColor;
    }

    private sealed record FlowLoadResult(bool IsValid, string? ErrorMessage);
}
