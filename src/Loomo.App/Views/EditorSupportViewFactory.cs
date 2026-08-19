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
        => new LoomoWebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            CreationProperties = creationProperties
        };

    public async Task<bool> InitializeAsync(WebView2CompositionControl view)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
            // ここでは NoteCreated しない——切り離しの複製プレビューは生成プロパティ無し（＝既定プロファイル）
            // で作るので必ず成功し、それで「動く環境がある」を立てると共有プロファイル側の立て直しが
            // 永久に封じられる。共有プロファイルで作れたことは呼び元（EditorSupportWebViewController）が知らせる。
            return view.TryCore() is not null;
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
