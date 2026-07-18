namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport の表示モデル生成に必要な、UI 非依存の入力。</summary>
public sealed record EditorSupportContext(
    string? FilePath,
    string Text,
    string WorkspaceRoot,
    string? ReadyPageKey,
    string PreviewTheme);

/// <summary>Provider の形式に依存せず View 層へ渡せる表示結果。</summary>
public sealed record EditorSupportResult(
    string Title,
    string? Html,
    string? Body,
    string? Uri,
    string? MapFolder,
    string? PageKey,
    bool ShowSlide,
    bool ShowOpenInBrowser,
    bool ShowExport);

/// <summary>Provider の出力を EditorSupport 共通の表示結果へ変換する。</summary>
public sealed class EditorSupportPipeline
{
    public bool SupportsHtmlExport(IEditorSupportProvider? provider)
        => provider is IEditorSupportHtmlProvider;

    public bool SupportsMarkdownExport(IEditorSupportProvider? provider)
        => provider is IEditorSupportMarkdownExportProvider;

    public async Task<string?> RenderPortableHtmlAsync(
        IEditorSupportProvider? provider,
        EditorSupportContext context,
        string? sourceDirectory,
        string assetsDirectory)
    {
        if (provider is not IEditorSupportHtmlProvider htmlProvider || context.FilePath is null)
            return null;

        var text = provider.UsesEditorText ? context.Text : string.Empty;
        return await Task.Run(() => PortableHtml.Build(
            htmlProvider.RenderHtml(context.FilePath, text), sourceDirectory, assetsDirectory));
    }

    public async Task<string?> RenderMarkdownAsync(
        IEditorSupportProvider? provider,
        EditorSupportContext context)
    {
        if (provider is not IEditorSupportMarkdownExportProvider markdownProvider || context.FilePath is null)
            return null;

        var text = provider.UsesEditorText ? context.Text : string.Empty;
        return await Task.Run(() => markdownProvider.RenderMarkdown(context.FilePath, text));
    }

    public async Task<EditorSupportResult> PrepareAsync(
        IEditorSupportProvider? provider,
        EditorSupportContext context)
    {
        var filePath = context.FilePath;
        var text = provider?.UsesEditorText == false ? string.Empty : context.Text;
        if (provider is IEditorSupportUriProvider uriProvider && filePath is not null)
        {
            return new EditorSupportResult(
                uriProvider.DescribeTitle(filePath), null, null,
                uriProvider.ResolveNavigationUri(filePath), null, null,
                ShowSlide: false, ShowOpenInBrowser: true, ShowExport: false);
        }

        if (provider is IEditorSupportHtmlProvider htmlProvider && filePath is not null)
        {
            var title = htmlProvider.DescribeTitle(filePath);
            var mapFolder = MarkdownPreviewPaths.Resolve(context.WorkspaceRoot, filePath).MapFolder;
            var incremental = htmlProvider as IEditorSupportIncrementalHtmlProvider;
            var pageKey = incremental?.PageContextKey(filePath, text);
            string? html = null;
            string? body = null;
            try
            {
                if (incremental is not null && pageKey == context.ReadyPageKey)
                    body = await Task.Run(() => incremental.RenderBody(filePath, text));
                else
                    html = await Task.Run(() => htmlProvider.RenderHtml(filePath, text));
            }
            catch (Exception ex)
            {
                pageKey = null;
                html = MarkdownRenderer.RenderToHtml(
                    $"## プレビューエラー\n\n変換中に例外が発生しました。\n\n```\n{ex}\n```",
                    title, context.PreviewTheme);
            }

            return new EditorSupportResult(title, html, body, null, mapFolder, pageKey,
                ShowSlide: provider is MarkdownEditorSupport,
                ShowOpenInBrowser: true,
                ShowExport: true);
        }

        const string fallbackTitle = "Editor Support";
        return new EditorSupportResult(
            fallbackTitle,
            MarkdownRenderer.RenderToHtml(
                "## Editor Support\n\nこのファイルに対応するサポートはありません。",
                fallbackTitle, context.PreviewTheme),
            null, null, null, null,
            ShowSlide: false, ShowOpenInBrowser: false, ShowExport: false);
    }
}
