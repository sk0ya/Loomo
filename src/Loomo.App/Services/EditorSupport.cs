using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport ペインへ表示するコンテンツの提供者。アクティブなエディタタブのファイルに
/// 対応する提供者が登録されていれば、EditorSupport ペインが自動でその内容を表示する
/// （Markdown ならプレビュー等）。新しい拡張子へ対応するには、表示方式に応じて
/// <see cref="IEditorSupportHtmlProvider"/>（WebView2 へ HTML）か
/// <see cref="IEditorSupportVisualProvider"/>（WPF コントロールをそのまま表示）を実装し、
/// App.xaml.cs の DI へ追加登録するだけでよい。
/// </summary>
public interface IEditorSupportProvider
{
    /// <summary>この provider が担当する拡張子（例: ".md"）。</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>ペインのヘッダーへ出す表示名（例: "Preview: README.md"）。</summary>
    string DescribeTitle(string filePath);
}

/// <summary>HTML を生成して EditorSupport ペインの WebView2 へ表示する提供者（Markdown プレビュー等）。</summary>
public interface IEditorSupportHtmlProvider : IEditorSupportProvider
{
    /// <summary>エディタの現在テキストから、表示用の完全な HTML ドキュメントを生成する。</summary>
    string RenderHtml(string filePath, string text);
}

/// <summary>
/// WPF コントロールをそのまま EditorSupport ペインへ表示する提供者（CSV/TSV グリッド等）。
/// ビューは提供者側が1つ保持して使い回す（ペインは1枚なので同時表示は常に1つ）。
/// </summary>
public interface IEditorSupportVisualProvider : IEditorSupportProvider
{
    /// <summary>ペインへ載せるビューを生成（初回）または再利用して返す。UI スレッドで呼ばれる。</summary>
    FrameworkElement GetOrCreateView();

    /// <summary>エディタの現在テキストをビューへ反映する。UI スレッドで呼ばれる。</summary>
    Task UpdateAsync(string filePath, string text);

    /// <summary>
    /// ビュー内での編集をエディタ本文へ書き戻すための通知（編集できない提供者は発火しなくてよい）。
    /// ShellWindow が購読し、追従中のエディタタブのテキストを差し替える。UI スレッドで発火する。
    /// </summary>
    event EventHandler<EditorSupportContentEdited>? ContentEdited;
}

/// <summary>ビジュアル提供者内の編集結果（エディタ本文へ書き戻す完全なテキスト）。</summary>
public sealed record EditorSupportContentEdited(string FilePath, string Text);

/// <summary>登録された <see cref="IEditorSupportProvider"/> からファイルに対応するものを解決する。</summary>
public sealed class EditorSupportRegistry
{
    private readonly IReadOnlyDictionary<string, IEditorSupportProvider> _providersByExtension;

    public EditorSupportRegistry(IEnumerable<IEditorSupportProvider> providers)
    {
        var map = new Dictionary<string, IEditorSupportProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            foreach (var extension in provider.SupportedExtensions)
            {
                var normalized = NormalizeExtension(extension);
                if (map.TryGetValue(normalized, out var existing))
                    throw new InvalidOperationException(
                        $"EditorSupport provider for '{normalized}' is already registered: " +
                        $"{existing.GetType().Name}, {provider.GetType().Name}");

                map.Add(normalized, provider);
            }
        }

        _providersByExtension = map;
    }

    /// <summary>ファイルに対応する最初の提供者を返す。未対応・パス無しは null。</summary>
    public IEditorSupportProvider? Resolve(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var extension = Path.GetExtension(filePath);
        return string.IsNullOrWhiteSpace(extension)
            ? null
            : _providersByExtension.GetValueOrDefault(NormalizeExtension(extension));
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Extension must not be empty.", nameof(extension));
        return normalized.StartsWith(".", StringComparison.Ordinal)
            ? normalized
            : "." + normalized;
    }
}

/// <summary>
/// プレビューの相対パス画像解決に使う、仮想ホストのマップ先フォルダと base href の決定。
/// base href を埋め込む <see cref="MarkdownEditorSupport"/> と、実際にマップする ShellWindow の
/// 両方がここを通ることで、判断が食い違わないようにする。
/// </summary>
public static class MarkdownPreviewPaths
{
    /// <summary>
    /// ファイルがワークスペースルート配下なら、ルートをマップ先にして base href をファイルの
    /// フォルダ位置（例: https://preview.loomo/docs/）にする。これで <c>../assets/img.png</c> のように
    /// ルート内で上のフォルダへ遡る画像も解決できる。ルート未設定・ルート外（別ドライブ含む）は
    /// 従来どおりファイルのフォルダをマップ先にする。
    /// </summary>
    public static (string MapFolder, string BaseHref) Resolve(string? workspaceRoot, string filePath)
    {
        var fileDir = Path.GetDirectoryName(filePath) ?? "";
        var hostRoot = $"https://{MarkdownRenderer.PreviewVirtualHost}/";

        if (string.IsNullOrWhiteSpace(workspaceRoot) || fileDir.Length == 0)
            return (fileDir, hostRoot);

        var rel = Path.GetRelativePath(workspaceRoot, fileDir);
        var outsideRoot = rel == ".."
            || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(rel); // 別ドライブだと GetRelativePath は絶対パスを返す

        if (outsideRoot)
            return (fileDir, hostRoot);

        var href = rel == "."
            ? hostRoot
            : hostRoot + string.Join('/',
                rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Select(Uri.EscapeDataString)) + "/";
        return (workspaceRoot, href);
    }
}

/// <summary>Markdown（.md / .markdown）のライブプレビュー。</summary>
public sealed class MarkdownEditorSupport : IEditorSupportHtmlProvider
{
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;
    private static readonly string[] Extensions = [".md", ".markdown"];

    public MarkdownEditorSupport(AiSettings settings, IWorkspaceService workspace)
    {
        _settings = settings;
        _workspace = workspace;
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Preview: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => MarkdownRenderer.RenderToHtml(
            text,
            DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme,
            // 相対パス画像の解決先。ShellWindow が同じ Resolve のマップ先を仮想ホストへ割り当てる。
            baseHref: MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).BaseHref);
}

/// <summary>画像ファイル（.png / .ico など）のプレビュー。</summary>
public sealed class ImageEditorSupport : IEditorSupportVisualProvider
{
    private static readonly string[] Extensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff"];

    private Grid? _view;
    private Image? _image;
    private TextBlock? _caption;

    public event EventHandler<EditorSupportContentEdited>? ContentEdited { add { } remove { } }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Image: {Path.GetFileName(filePath)}";

    public FrameworkElement GetOrCreateView()
    {
        if (_view is not null)
            return _view;

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var imageHost = new Border
        {
            Margin = new Thickness(20, 20, 20, 8),
            Background = CreateCheckerBrush(),
            Child = _image
        };

        _caption = new TextBlock
        {
            Margin = new Thickness(20, 0, 20, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(154, 160, 166)),
            FontSize = 12
        };

        _view = new Grid();
        _view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _view.Children.Add(imageHost);
        Grid.SetRow(imageHost, 0);
        _view.Children.Add(_caption);
        Grid.SetRow(_caption, 1);
        return _view;
    }

    public Task UpdateAsync(string filePath, string text)
    {
        GetOrCreateView();
        if (_image is null || _caption is null)
            return Task.CompletedTask;

        _caption.Text = Path.GetFileName(filePath);
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            _image.Source = bitmap;
        }
        catch
        {
            _image.Source = null;
            _caption.Text = $"{Path.GetFileName(filePath)}\n画像を読み込めませんでした。";
        }

        return Task.CompletedTask;
    }

    private static Brush CreateCheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(48, 49, 52)),
            null,
            new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(37, 37, 38)),
            null,
            new GeometryGroup
            {
                Children =
                {
                    new RectangleGeometry(new Rect(0, 0, 12, 12)),
                    new RectangleGeometry(new Rect(12, 12, 12, 12))
                }
            }));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
