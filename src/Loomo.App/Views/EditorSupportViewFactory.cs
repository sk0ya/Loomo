namespace sk0ya.Loomo.App.Views;

/// <summary>EditorSupport 用 WebView2 の生成、Core 初期化、破棄を一元化する。</summary>
public interface IEditorSupportViewFactory
{
    WebView2CompositionControl Create(CoreWebView2CreationProperties? creationProperties = null);
    Task<bool> InitializeAsync(WebView2CompositionControl view);
    void Dispose(WebView2CompositionControl? view);
}

public sealed class EditorSupportViewFactory : IEditorSupportViewFactory
{
    public WebView2CompositionControl Create(CoreWebView2CreationProperties? creationProperties = null)
        => new()
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            CreationProperties = creationProperties
        };

    public async Task<bool> InitializeAsync(WebView2CompositionControl view)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
            return view.CoreWebView2 is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose(WebView2CompositionControl? view)
    {
        if (view is null)
            return;
        try { view.Dispose(); }
        catch { }
    }
}
