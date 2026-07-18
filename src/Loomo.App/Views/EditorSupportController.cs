namespace sk0ya.Loomo.App.Views;

/// <summary>EditorSupport の追従元、ピン留め、ファイル履歴を所有する機能コントローラー。</summary>
internal sealed class EditorSupportController
{
    private FrameworkElement? _visual;
    private readonly HashSet<IEditorSupportVisualProvider> _editSubscribed = new();

    internal EditorSupportController() => WebView = null!;
    public EditorSupportController(EditorSupportWebViewController webView) => WebView = webView;

    public EditorSupportWebViewController WebView { get; }
    public EditorTab? Source { get; private set; }
    public bool IsPinned { get; set; }
    public bool IsNavigating { get; set; }
    public EditorSupportHistory History { get; } = new();

    public async Task ShowVisualAsync(
        Panel host,
        IEditorSupportVisualProvider provider,
        string filePath,
        string text,
        EventHandler<EditorSupportContentEdited> contentEdited)
    {
        if (_editSubscribed.Add(provider))
            provider.ContentEdited += contentEdited;
        var view = provider.GetOrCreateView();
        ShowVisual(host, view);
        await provider.UpdateAsync(filePath, provider.UsesEditorText ? text : string.Empty);
    }

    public void ShowVisual(Panel host, FrameworkElement view)
    {
        if (!ReferenceEquals(_visual, view))
        {
            if (_visual is not null)
                host.Children.Remove(_visual);
            host.Children.Add(view);
            _visual = view;
        }
        view.Visibility = Visibility.Visible;
        if (WebView.View is not null)
            WebView.View.Visibility = Visibility.Collapsed;
    }

    public void ShowWebView()
    {
        if (_visual is not null)
            _visual.Visibility = Visibility.Collapsed;
        if (WebView.View is not null)
            WebView.View.Visibility = Visibility.Visible;
    }

    public bool TryChangeSource(EditorTab source, bool force, out EditorTab? previous)
    {
        previous = Source;
        if (ReferenceEquals(Source, source))
            return false;
        if (IsPinned && !force && Source is not null)
            return false;
        Source = source;
        if (!IsNavigating)
            History.Navigate(source.PeekFilePath);
        return true;
    }

    public EditorTab? DetachSource()
    {
        var previous = Source;
        Source = null;
        return previous;
    }

    public async Task NavigateHistoryAsync(
        bool back,
        IReadOnlyList<EditorTab> openTabs,
        Action<EditorTab> activate,
        Func<string, Task> openFile)
    {
        IsNavigating = true;
        try
        {
            while ((back ? History.GoBack() : History.GoForward()) is { } path)
            {
                var open = openTabs.FirstOrDefault(tab =>
                    string.Equals(tab.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
                if (open is not null)
                {
                    activate(open);
                    return;
                }
                if (File.Exists(path))
                {
                    await openFile(path);
                    return;
                }
            }
        }
        finally
        {
            IsNavigating = false;
        }
    }

    public void Reset()
    {
        Source = null;
        IsPinned = false;
        IsNavigating = false;
    }

    public async Task<EditorSupportWebContent> PrepareWebContentAsync(
        IEditorSupportProvider? provider,
        string? filePath,
        string text,
        string workspaceRoot,
        string? readyPageKey,
        string previewTheme)
    {
        if (provider is IEditorSupportUriProvider uriProvider && filePath is not null)
        {
            return new EditorSupportWebContent(
                uriProvider.DescribeTitle(filePath), null, null,
                uriProvider.ResolveNavigationUri(filePath), null, null,
                ShowSlide: false, ShowOpenInBrowser: true, ShowExport: false);
        }

        if (provider is IEditorSupportHtmlProvider htmlProvider && filePath is not null)
        {
            var title = htmlProvider.DescribeTitle(filePath);
            var mapFolder = MarkdownPreviewPaths.Resolve(workspaceRoot, filePath).MapFolder;
            var incremental = htmlProvider as IEditorSupportIncrementalHtmlProvider;
            var pageKey = incremental?.PageContextKey(filePath, text);
            string? html = null;
            string? body = null;
            try
            {
                if (incremental is not null && pageKey == readyPageKey)
                    body = await Task.Run(() => incremental.RenderBody(filePath, text));
                else
                    html = await Task.Run(() => htmlProvider.RenderHtml(filePath, text));
            }
            catch (Exception ex)
            {
                pageKey = null;
                html = MarkdownRenderer.RenderToHtml(
                    $"## プレビューエラー\n\n変換中に例外が発生しました。\n\n```\n{ex}\n```",
                    title, previewTheme);
            }

            return new EditorSupportWebContent(title, html, body, null, mapFolder, pageKey,
                ShowSlide: provider is MarkdownEditorSupport,
                ShowOpenInBrowser: true,
                ShowExport: true);
        }

        const string fallbackTitle = "Editor Support";
        return new EditorSupportWebContent(
            fallbackTitle,
            MarkdownRenderer.RenderToHtml(
                "## Editor Support\n\nこのファイルに対応するサポートはありません。",
                fallbackTitle, previewTheme),
            null, null, null, null,
            ShowSlide: false, ShowOpenInBrowser: false, ShowExport: false);
    }
}

internal sealed record EditorSupportWebContent(
    string Title,
    string? Html,
    string? Body,
    string? Uri,
    string? MapFolder,
    string? PageKey,
    bool ShowSlide,
    bool ShowOpenInBrowser,
    bool ShowExport);
