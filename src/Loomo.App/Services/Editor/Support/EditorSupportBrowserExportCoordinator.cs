using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport の提供者を準備し、URLまたはHTMLの表示先へ結果を渡す。</summary>
internal static class EditorSupportBrowserExportCoordinator
{
    public static async Task OpenAsync(
        EditorTab? source,
        EditorSupportResolver resolver,
        EditorSupportPipeline pipeline,
        IWorkspaceService workspace,
        string previewTheme,
        Func<string, string?, Task> openUrl,
        Func<string, string?, string, Task> openSnapshot)
    {
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;

        var provider = resolver.Resolve(filePath).Provider;
        var result = await pipeline.PrepareAsync(provider, EditorSupportContext.For(
            workspace, filePath, source.Control.Text, null, previewTheme));
        if (result.Uri is { } uri)
        {
            await openUrl(uri, result.Title);
            return;
        }
        if (result.Html is { } html)
            await openSnapshot(html, result.MapFolder, result.Title);
    }
}
