using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

public sealed class TabIconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint ShgfiSmallIcon = 0x000000001;

    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;

    private readonly ConcurrentDictionary<string, ImageSource> _shellIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource>>> _browserIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageSource _defaultFileIcon;
    private readonly ImageSource _defaultBrowserIcon;
    private readonly string _terminalExecutablePath;

    public TabIconService()
    {
        _defaultFileIcon = LoadShellIcon(".txt", useFileAttributes: true, fileAttributes: FileAttributeNormal) ?? CreateFallbackDocumentIcon();
        _defaultBrowserIcon = LoadBrowserFallbackIcon() ?? _defaultFileIcon;
        _terminalExecutablePath = ResolveTerminalExecutablePath();
    }

    public ImageSource GetTerminalIcon()
        => GetShellIcon(_terminalExecutablePath, useFileAttributes: false) ?? _defaultFileIcon;

    public ImageSource GetFileIcon(string? path)
    {
        var key = string.IsNullOrWhiteSpace(path)
            ? "__file:default"
            : NormalizePathKey(path);

        return _shellIconCache.GetOrAdd(key, _ =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return _defaultFileIcon;

            if (Directory.Exists(path))
                return LoadShellIcon(path, useFileAttributes: true, fileAttributes: FileAttributeDirectory) ?? _defaultFileIcon;

            return File.Exists(path)
                ? LoadShellIcon(path, useFileAttributes: false, fileAttributes: FileAttributeNormal) ?? _defaultFileIcon
                : LoadShellIcon(path, useFileAttributes: true, fileAttributes: FileAttributeNormal) ?? _defaultFileIcon;
        });
    }

    public ImageSource GetBrowserDefaultIcon() => _defaultBrowserIcon;

    public Task<ImageSource> GetBrowserIconAsync(CoreWebView2? coreWebView2, string? pageUrl)
    {
        if (coreWebView2 is null)
            return Task.FromResult(_defaultBrowserIcon);

        var cacheKey = GetBrowserCacheKey(coreWebView2.FaviconUri, pageUrl);
        if (string.IsNullOrWhiteSpace(cacheKey))
            return Task.FromResult(_defaultBrowserIcon);

        return _browserIconCache.GetOrAdd(cacheKey, _ =>
            new Lazy<Task<ImageSource>>(() => LoadBrowserIconAsync(coreWebView2), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private static string ResolveTerminalExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "pwsh.exe";
    }

    private static string? GetBrowserCacheKey(string? faviconUri, string? pageUrl)
    {
        if (!string.IsNullOrWhiteSpace(faviconUri))
            return faviconUri.Trim();

        return string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl.Trim();
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path, Environment.CurrentDirectory);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static ImageSource? LoadShellIcon(string path, bool useFileAttributes, uint fileAttributes)
    {
        var info = new SHFILEINFO();
        var flags = ShgfiIcon | ShgfiSmallIcon | (useFileAttributes ? ShgfiUseFileAttributes : 0);
        var result = SHGetFileInfo(path, fileAttributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private ImageSource? GetShellIcon(string path, bool useFileAttributes)
    {
        if (_shellIconCache.TryGetValue(path, out var cached))
            return cached;

        var icon = LoadShellIcon(path, useFileAttributes, FileAttributeNormal);
        if (icon is not null)
            _shellIconCache[path] = icon;

        return icon;
    }

    private static async Task<ImageSource> LoadBrowserIconAsync(CoreWebView2 coreWebView2)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(coreWebView2.FaviconUri))
                return LoadBrowserFallbackIcon() ?? CreateFallbackDocumentIcon();

            await using var stream = await coreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null)
                return LoadBrowserFallbackIcon() ?? CreateFallbackDocumentIcon();

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return LoadBrowserFallbackIcon() ?? CreateFallbackDocumentIcon();
        }
    }

    private static ImageSource? LoadBrowserFallbackIcon()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return LoadShellIcon(candidate, useFileAttributes: false, fileAttributes: FileAttributeNormal);
        }

        return CreateFallbackBrowserIcon();
    }

    private static ImageSource CreateFallbackDocumentIcon()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        brush.Freeze();

        var accent = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
        accent.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(null, new Pen(accent, 1.2),
            Geometry.Parse("M4,1.5 H10 L13,4.5 V14.5 H4 Z M10,1.5 V5 H13")));
        group.Children.Add(new GeometryDrawing(null, new Pen(brush, 1.2),
            Geometry.Parse("M5.5,7.5 H11.5 M5.5,10 H11.5")));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static ImageSource CreateFallbackBrowserIcon()
    {
        var outer = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
        outer.Freeze();

        var inner = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        inner.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(null, new Pen(outer, 1.1), Geometry.Parse("M8,1.5 A6.5,6.5 0 1 1 7.999,1.5 Z")));
        group.Children.Add(new GeometryDrawing(null, new Pen(inner, 1.0), Geometry.Parse("M2.5,8 H13.5 M8,2.5 C6.2,4.3 5.2,6.3 5.2,8 C5.2,9.7 6.2,11.7 8,13.5 M8,2.5 C9.8,4.3 10.8,6.3 10.8,8 C10.8,9.7 9.8,11.7 8,13.5")));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
