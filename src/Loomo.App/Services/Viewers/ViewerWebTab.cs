using sk0ya.Loomo.Core.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// URL を渡して開いたタブ（§44.2「パスから開く」）。ブラウザペインと同じく、その頁をこのタブの中で見せる（中での移動は自由）。
/// 部屋の API は差し込まない。新しい窓を開こうとした頁はブラウザペインの新しいタブへ。保存する鍵は、いま見ている URL。
/// </summary>
internal sealed class ViewerWebTab : ViewerTabBase
{
    private readonly IEditorSupportViewFactory _factory;
    private readonly Func<WebView2CompositionControl> _createView;
    private readonly Func<string, Task> _openUrl;
    private WebView2CompositionControl? _web;
    private string _url;
    private string? _title;
    private string? _error;

    /// <param name="createView">ブラウザペインと同じプロファイルの WebView2（ログインがそのまま効く）。</param>
    public ViewerWebTab(string url, IEditorSupportViewFactory factory, Func<WebView2CompositionControl> createView,
        Panel host, Dispatcher dispatcher, Func<string, Task> openUrl)
        : base(null, host, dispatcher)
    {
        _url = url;
        _factory = factory;
        _createView = createView;
        _openUrl = openUrl;
    }

    public string Url => _url;
    public override string Key => ViewerPath.UrlKeyOf(_url);
    public override string Title => _title is { Length: > 0 } title ? title
        : Uri.TryCreate(_url, UriKind.Absolute, out var uri) ? uri.Host : _url;
    public override string ToolTip => _url + "\nブラウザと同じ中身をこのタブに表示";

    public override UIElement? BuildCard()
    {
        if (Status != ViewerTabStatus.Broken)
            return null;
        var panel = ViewerCards.Panel();
        panel.Children.Add(ViewerCards.Line("この頁を出せません", "Fs14", "Fg", bold: true));
        panel.Children.Add(ViewerCards.Mono(_error ?? ""));
        panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button("もう一度", "SecondaryButton", () =>
        {
            Unmount();
            SetStatus(ViewerTabStatus.Pending);
            OnShownChanged();
        })));
        return panel;
    }

    public override Task RefreshAsync(bool reload = false)
    {
        if (reload && _web.TryCore() is { } core)
        {
            try { core.Reload(); } catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
        }
        else
            OnShownChanged();
        return Task.CompletedTask;
    }

    public override void Focus() => _web?.Focus();

    protected override void OnShownChanged()
    {
        if (!CanShow)
        {
            if (_web is not null)
                _web.Visibility = Visibility.Collapsed;
            return;
        }
        if (Status == ViewerTabStatus.Broken)
            return;
        if (_web is null)
            _ = LoadAsync();
        else
            _web.Visibility = Visibility.Visible;
    }

    private async Task LoadAsync()
    {
        var web = _web = _createView();
        web.Visibility = Visibility.Collapsed;
        Host.Children.Add(web);
        if (!await _factory.InitializeAsync(web) || web.TryCore() is not { } core || !ReferenceEquals(_web, web))
        {
            if (ReferenceEquals(_web, web))
            {
                _error = "WebView2 could not be started.";
                Unmount();
                SetStatus(ViewerTabStatus.Broken);
            }
            return;
        }
        core.DocumentTitleChanged += (_, _) =>
        {
            _title = core.DocumentTitle;
            RaiseChanged();
        };
        core.SourceChanged += (_, _) =>
        {
            if (core.Source is { Length: > 0 } source && Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                _url = source;
                RaiseChanged();
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated)
                _ = _openUrl(e.Uri);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                Unmount();
                OnShownChanged();
            });
        };
        web.Visibility = CanShow ? Visibility.Visible : Visibility.Collapsed;
        try { core.Navigate(_url); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException) { _error = ex.Message; }
        SetStatus(ViewerTabStatus.Running);
    }

    protected override void Unmount()
    {
        if (_web is null)
            return;
        Host.Children.Remove(_web);
        _factory.Dispose(_web);
        _web = null;
    }
}
