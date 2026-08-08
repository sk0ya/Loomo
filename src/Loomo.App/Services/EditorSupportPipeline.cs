namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport の表示モデル生成に必要な、UI 非依存の入力。</summary>
public sealed record EditorSupportContext(
    string? FilePath,
    string Text,
    /// <summary>相対パス（画像・リンク）の解決基準。マルチルートではその<b>ファイルを担当する</b>
    /// ワークスペースフォルダー。自分で詰めず <see cref="EditorSupportContext.For"/> を使うこと。</summary>
    string BaseFolder,
    string? ReadyPageKey,
    string PreviewTheme)
{
    /// <summary>ワークスペースから組み立てる。<b>ホストはこちらを使うこと。</b>
    /// <see cref="BaseFolder"/> を <paramref name="filePath"/> から導くので、
    /// 「別のファイルの基準で描く」食い違いを書けない（§32.10.1）。</summary>
    public static EditorSupportContext For(
        IWorkspaceService workspace, string? filePath, string text,
        string? readyPageKey, string previewTheme)
        => new(filePath, text, workspace.FolderForOrPrimary(filePath) ?? string.Empty,
            readyPageKey, previewTheme);
}

/// <summary>Provider の形式に依存せず View 層へ渡せる表示結果。</summary>
public sealed record EditorSupportResult(
    string Title,
    string? Html,
    string? Body,
    string? Uri,
    string? MapFolder,
    string? PageKey,
    bool ShowSlide,
    /// <summary>アウトライン（見出し一覧）の表示トグルをヘッダーへ出すか（Markdown プレビューのみ）。</summary>
    bool ShowOutline,
    bool ShowOpenInBrowser,
    bool ShowExport,
    IEditorSupportVisualProvider? VisualProvider = null);

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
        // ビジュアル表示は HTML を持たない。表示インスタンスの生成・載せ替えは呼び元（表示面）の役目で、
        // 表示面ごとに実体を作れるので切り離しウィンドウでも同じ提供者をそのまま使える
        // （以前ここで返していた「複製に未対応です」の代替ページは不要になった）。
        if (provider is IEditorSupportVisualProvider visualProvider && filePath is not null)
        {
            return new EditorSupportResult(
                visualProvider.DescribeTitle(filePath),
                null, null, null, null, null,
                ShowSlide: false, ShowOutline: false, ShowOpenInBrowser: false, ShowExport: false,
                VisualProvider: visualProvider);
        }

        if (provider is IEditorSupportUriProvider uriProvider && filePath is not null)
        {
            return new EditorSupportResult(
                uriProvider.DescribeTitle(filePath), null, null,
                uriProvider.ResolveNavigationUri(filePath), null, null,
                ShowSlide: false, ShowOutline: false, ShowOpenInBrowser: true, ShowExport: false);
        }

        if (provider is IEditorSupportHtmlProvider htmlProvider && filePath is not null)
        {
            var title = htmlProvider.DescribeTitle(filePath);
            var mapFolder = MarkdownPreviewPaths.Resolve(context.BaseFolder, filePath).MapFolder;
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
                // アウトラインは通常ドキュメント表示のトグル。marp スライドでは効かないので出さない
                // （出すと「押せるのに何も起きないボタン」になる）。
                ShowOutline: provider is MarkdownEditorSupport && !MarkdownRenderer.IsMarpDocument(text),
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
            ShowSlide: false, ShowOutline: false, ShowOpenInBrowser: false, ShowExport: false);
    }
}
