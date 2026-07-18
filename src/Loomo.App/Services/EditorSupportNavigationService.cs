namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupportの一時ページとWebView2仮想ホストを管理する。</summary>
public sealed class EditorSupportNavigationService
{
    private readonly string _previewFolder;
    private long _pageVersion;
    private string? _mappedPreviewFolder;

    public EditorSupportNavigationService(string previewFolder) => _previewFolder = previewFolder;

    public bool TryWritePage(string html, out string url)
    {
        url = "";
        try
        {
            Directory.CreateDirectory(_previewFolder);
            File.WriteAllText(Path.Combine(_previewFolder, "preview.html"), html, System.Text.Encoding.UTF8);
            url = $"https://{MarkdownRenderer.PageVirtualHost}/preview.html?v={++_pageVersion}";
            return true;
        }
        catch { return false; }
    }

    public static bool IsPreviewUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && string.Equals(uri.Host, MarkdownRenderer.PageVirtualHost, StringComparison.OrdinalIgnoreCase);

    public static void ConfigureVirtualHosts(CoreWebView2 core, string? mapFolder)
    {
        TryMap(core, MarkdownRenderer.AssetsVirtualHost,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Web"));
        if (!string.IsNullOrEmpty(mapFolder))
            TryMap(core, MarkdownRenderer.PreviewVirtualHost, mapFolder);
        var pageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Loomo", "EditorSupportPreview");
        try { Directory.CreateDirectory(pageFolder); } catch { }
        TryMap(core, MarkdownRenderer.PageVirtualHost, pageFolder);
    }

    public void UpdatePreviewHost(CoreWebView2 core, string? folder)
    {
        if (string.IsNullOrEmpty(folder)
            || string.Equals(folder, _mappedPreviewFolder, StringComparison.OrdinalIgnoreCase))
            return;
        TryMap(core, MarkdownRenderer.PreviewVirtualHost, folder);
        _mappedPreviewFolder = folder;
    }

    private static void TryMap(CoreWebView2 core, string host, string folder)
    {
        try
        {
            core.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch { }
    }
}
