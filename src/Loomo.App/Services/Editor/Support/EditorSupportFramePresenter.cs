using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>完成した EditorSupport フレームを WPF の表示面へ同期適用する。</summary>
internal sealed class EditorSupportFramePresenter
{
    private readonly EditorSupportController _controller;
    private readonly EditorSupportWebViewController _webView;
    private readonly Panel _contentHost;
    private readonly FrameworkElement _settingsButton;
    private readonly ToggleButton _slideToggle;
    private readonly ToggleButton _outlineToggle;
    private readonly ToggleButton _editButton;
    private readonly FrameworkElement _openInBrowserButton;
    private readonly FrameworkElement _exportButton;
    private readonly TextBlock _title;
    private readonly Func<bool> _isSlideMode;
    private readonly Func<bool> _isOutlineVisible;
    private readonly Func<CodeOutlineView> _ensureOutlineView;
    private readonly Action<FrameworkElement> _showVisual;
    private readonly Action _hideVisual;
    private readonly Action _requestFullPage;
    private readonly Action _captureWebThumbnail;

    public EditorSupportFramePresenter(
        EditorSupportController controller,
        EditorSupportWebViewController webView,
        Panel contentHost,
        FrameworkElement settingsButton,
        ToggleButton slideToggle,
        ToggleButton outlineToggle,
        ToggleButton editButton,
        FrameworkElement openInBrowserButton,
        FrameworkElement exportButton,
        TextBlock title,
        Func<bool> isSlideMode,
        Func<bool> isOutlineVisible,
        Func<CodeOutlineView> ensureOutlineView,
        Action<FrameworkElement> showVisual,
        Action hideVisual,
        Action requestFullPage,
        Action captureWebThumbnail)
    {
        _controller = controller;
        _webView = webView;
        _contentHost = contentHost;
        _settingsButton = settingsButton;
        _slideToggle = slideToggle;
        _outlineToggle = outlineToggle;
        _editButton = editButton;
        _openInBrowserButton = openInBrowserButton;
        _exportButton = exportButton;
        _title = title;
        _isSlideMode = isSlideMode;
        _isOutlineVisible = isOutlineVisible;
        _ensureOutlineView = ensureOutlineView;
        _showVisual = showVisual;
        _hideVisual = hideVisual;
        _requestFullPage = requestFullPage;
        _captureWebThumbnail = captureWebThumbnail;
    }

    public void UpdateHeader(
        bool showSlide, bool showOutline, bool showEdit, bool showOpenInBrowser, bool showExport)
    {
        _slideToggle.Visibility = showSlide ? Visibility.Visible : Visibility.Collapsed;
        _outlineToggle.Visibility = showOutline ? Visibility.Visible : Visibility.Collapsed;
        _editButton.Visibility = showEdit ? Visibility.Visible : Visibility.Collapsed;
        _editButton.IsChecked = showEdit && _webView.MarkdownEditMode;
        _editButton.ToolTip = _webView.MarkdownEditMode
            ? "プレビューに戻る"
            : "Markdownをこの画面で直接編集";
        _openInBrowserButton.Visibility = showOpenInBrowser ? Visibility.Visible : Visibility.Collapsed;
        _exportButton.Visibility = showExport ? Visibility.Visible : Visibility.Collapsed;
        _slideToggle.IsChecked = _isSlideMode();
        _outlineToggle.IsChecked = _isOutlineVisible();
    }

    public void ToggleMarkdownEditMode()
    {
        _webView.SetMarkdownEditMode(!_webView.MarkdownEditMode);
        _editButton.IsChecked = _webView.MarkdownEditMode;
        _editButton.ToolTip = _webView.MarkdownEditMode
            ? "プレビューに戻る"
            : "Markdownをこの画面で直接編集";
    }

    /// <summary>途中で部分適用せず、WebView準備とパネル構造を確認してから一括反映する。</summary>
    public bool Apply(EditorSupportFrame frame)
    {
        var core = _webView.Core;
        if (frame.Content is EditorSupportFrameContent.WebContent && core is null)
        {
            CodeSupportDiag.Log("webview unavailable: frame dropped");
            return false;
        }

        _settingsButton.Visibility = Visibility.Collapsed;
        if (!frame.ShowEdit)
            _webView.SetMarkdownEditMode(false);
        UpdateHeader(frame.ShowSlide, frame.ShowOutline, frame.ShowEdit,
            frame.ShowOpenInBrowser, frame.ShowExport);
        _title.Text = frame.Title;

        switch (frame.Content)
        {
            case EditorSupportFrameContent.VisualContent visual:
                _controller.MountVisual(_contentHost, visual.Visual);
                _settingsButton.Visibility = visual.Visual is IEditorSupportSettingsVisual
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                visual.Apply();
                break;
            case EditorSupportFrameContent.OutlineContent outline:
                var outlineView = _ensureOutlineView();
                _showVisual(outlineView);
                outlineView.ShowOutline(outline.Roots, outline.CurrentLine1, outline.Panels);
                break;
            case EditorSupportFrameContent.PanelsContent panels:
                if (_controller.OutlineView is not { } shown)
                    return false;
                if (panels.CurrentLine1 is int current)
                    shown.SetCurrentAndPanels(current, panels.Panels);
                else
                    shown.SetPanels(panels.Panels);
                break;
            case EditorSupportFrameContent.NoticeContent notice:
                var noticeView = _ensureOutlineView();
                _showVisual(noticeView);
                noticeView.ShowNotice(notice.Notice);
                break;
            case EditorSupportFrameContent.WebContent web:
                _hideVisual();
                if (_webView.Show(core!, web) == EditorSupportPageApplyResult.NeedsFullPage)
                {
                    _requestFullPage();
                    break;
                }
                _captureWebThumbnail();
                break;
        }

        return true;
    }
}
