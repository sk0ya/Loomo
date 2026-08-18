namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupportの一時ページとWebView2仮想ホストを管理する。</summary>
public sealed class EditorSupportNavigationService
{
    /// <summary>一時ページのファイル名。フォルダーは全インスタンス共通（＝プロファイルの下）なので、
    /// Loomo を2つ起動したときに互いの本文を上書きし合わないようプロセスごとに分ける。</summary>
    private readonly string _pageFileName;
    private readonly string _previewFolder;
    private long _pageVersion;
    private string? _mappedPreviewFolder;

    public EditorSupportNavigationService(string previewFolder)
        : this(previewFolder, $"preview-{Environment.ProcessId}.html") { }

    internal EditorSupportNavigationService(string previewFolder, string pageFileName)
    {
        _previewFolder = previewFolder;
        _pageFileName = pageFileName;
    }

    public bool TryWritePage(string html, out string url)
    {
        url = "";
        try
        {
            Directory.CreateDirectory(_previewFolder);
            File.WriteAllText(Path.Combine(_previewFolder, _pageFileName), html, System.Text.Encoding.UTF8);
            url = $"https://{MarkdownRenderer.PageVirtualHost}/{_pageFileName}?v={++_pageVersion}";
            return true;
        }
        catch { return false; }
    }

    /// <summary>もう誰も使っていない一時ページ（落ちたインスタンスの置き土産）を片付ける。
    /// 消せなくても構わない——プレビューの表示には関係しない掃除なので、失敗は黙って捨てる。</summary>
    public void CleanStalePages(TimeSpan olderThan)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_previewFolder, "preview-*.html"))
            {
                if (string.Equals(Path.GetFileName(file), _pageFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > olderThan)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    public static bool IsPreviewUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && string.Equals(uri.Host, MarkdownRenderer.PageVirtualHost, StringComparison.OrdinalIgnoreCase);

    public void ConfigureVirtualHosts(CoreWebView2 core, string? mapFolder)
    {
        TryMap(core, MarkdownRenderer.AssetsVirtualHost,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Web"));
        if (!string.IsNullOrEmpty(mapFolder))
            TryMap(core, MarkdownRenderer.PreviewVirtualHost, mapFolder);
        // TryWritePage と同じフォルダーを公開しないと一時ページを読み込めない。
        try { Directory.CreateDirectory(_previewFolder); } catch { }
        TryMap(core, MarkdownRenderer.PageVirtualHost, _previewFolder);
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
