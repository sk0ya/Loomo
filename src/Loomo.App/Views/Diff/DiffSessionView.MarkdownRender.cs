using System;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>DiffSessionView の Markdown 描画用 WebView を controller に委譲する。</summary>
public partial class DiffSessionView
{
    /// <summary>レンダリング差分本文からのリンク。遷移先の解決は ShellWindow が行う。</summary>
    public event EventHandler<(string Href, string? SourcePath)>? MarkdownLinkClicked;

    /// <summary>切り離しホストなどから WebView2 の factory と一時ページの保存先を受け取る。</summary>
    public void ConfigureMarkdownRender(
        IEditorSupportViewFactory factory, string previewFolder, string? instanceKey = null)
        => _markdownRenderController.Configure(factory, previewFolder, instanceKey);

    private void OnMarkdownRenderLinkClicked(
        object? sender, (string Href, string? SourcePath) args)
        => MarkdownLinkClicked?.Invoke(this, args);

    public void Dispose()
    {
        _bindingController.Dispose();
        _markdownRenderController.Dispose();
        _documentBuildController.Dispose();
    }
}
