using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// パスから開いた、EditorSupport が見せられる形式（PDF・画像・Markdown・CSV・Excel…）のタブ（§44.2「パスから開く」）。
/// 表示は EditorSupport と同じ部品（<see cref="EditorSupportResolver"/>・<see cref="EditorSupportPipeline"/>・
/// <see cref="EditorSupportVisualHost"/>）で、エディタではなくディスクの中身を描く。<b>読むだけ</b>——表の編集などは
/// ファイルへ書き戻さない（書き換えたいときはエディタで開く）。ファイルが書き換わったら描き直す。
/// </summary>
internal sealed class ViewerPreviewTab : ViewerTabBase
{
    private const long MaxTextBytes = 8 * 1024 * 1024;
    private readonly EditorSupportResolver _resolver;
    private readonly EditorSupportPipeline _pipeline;
    private readonly IEditorSupportViewFactory _factory;
    private readonly LoomoSettings _settings;
    private readonly IWorkspaceService _workspace;
    private readonly Func<string, string?, Task> _linkClicked;
    private readonly SingleFileWatcher _fileWatcher;
    private readonly Grid _root = new();
    private EditorSupportVisualHost? _visuals;
    private WebView2CompositionControl? _web;
    private FrameworkElement? _mountedVisual;
    private Task<bool>? _init;
    private string? _mappedFolder;
    private int _renderSeq;
    private bool _stale = true;

    public ViewerPreviewTab(string targetPath, EditorSupportResolver resolver, EditorSupportPipeline pipeline,
        IEditorSupportViewFactory factory, LoomoSettings settings, IWorkspaceService workspace,
        Func<string, string?, Task> linkClicked, Panel host, Dispatcher dispatcher)
        : base(targetPath, host, dispatcher)
    {
        _resolver = resolver;
        _pipeline = pipeline;
        _factory = factory;
        _settings = settings;
        _workspace = workspace;
        _linkClicked = linkClicked;
        _fileWatcher = new SingleFileWatcher(changedPath =>
        {
            _stale = true;
            if (CanShow)
                _ = RenderAsync();
        }, post: action => dispatcher.BeginInvoke(action));
        _fileWatcher.Watch(targetPath);
    }

    private string Path => TargetPath!;

    public override string ToolTip => Path + "\nEditorSupport と同じ表示（読むだけ）";

    public override UIElement? BuildCard() => null;

    /// <summary>Hex で出しているもの（見せ方の決まっていないバイナリ）は、Claude Code に見せ方を作ってもらえる。</summary>
    public override bool CanGenerate => _resolver.Resolve(Path).Provider is HexEditorSupport;

    public override Task RefreshAsync(bool reload = false)
    {
        if (reload)
            _stale = true;
        OnShownChanged();
        return Task.CompletedTask;
    }

    public override void Focus()
    {
        if (_mountedVisual is { Visibility: Visibility.Visible } visual)
            visual.Focus();
        else
            _web?.Focus();
    }

    protected override void OnShownChanged()
    {
        if (!CanShow)
        {
            _root.Visibility = Visibility.Collapsed;
            return;
        }
        if (!Host.Children.Contains(_root))
            Host.Children.Add(_root);
        _root.Visibility = Visibility.Visible;
        if (_stale)
            _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        var seq = ++_renderSeq;
        _stale = false;
        var filePath = Path;
        var selection = _resolver.Resolve(filePath);
        var provider = selection.Provider;
        var text = provider?.UsesEditorText == true ? await Task.Run(() => ReadText(filePath)) : string.Empty;
        if (seq != _renderSeq || Disposed)
            return;

        if (provider is IEditorSupportVisualProvider visualProvider)
        {
            try
            {
                _visuals ??= new EditorSupportVisualHost();
                var visual = _visuals.GetOrCreate(visualProvider);
                var apply = await visual.PrepareAsync(filePath, visualProvider.UsesEditorText ? text : string.Empty, CancellationToken.None);
                if (seq != _renderSeq || Disposed)
                    return;
                MountVisual(visual.View);
                apply();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* 表示できなければ前の表示のまま */ }
            return;
        }

        HideVisual();
        if (await EnsureWebAsync() is not { } web || web.TryCore() is not { } core || seq != _renderSeq)
            return;
        var result = await _pipeline.PrepareAsync(provider, EditorSupportContext.For(
            _workspace, filePath, text, null, _settings.Appearance.MarkdownPreviewTheme));
        if (seq != _renderSeq || Disposed)
            return;
        try
        {
            if (result.Uri is { } uri)
                core.Navigate(uri);
            else
            {
                MapPreviewFolder(core, result.MapFolder);
                if (result.Html is { } html)
                    core.NavigateToString(html);   // 約 2MB の上限は EditorSupport と同じ
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException) { }
        if (Status != ViewerTabStatus.Running)
            SetStatus(ViewerTabStatus.Running);
    }

    private static string ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTextBytes)
                return $"（{MaxTextBytes / 1024 / 1024} MB より大きいので読みません）";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "（読めませんでした：" + ex.Message + "）";
        }
    }

    private void MountVisual(FrameworkElement view)
    {
        if (!ReferenceEquals(_mountedVisual, view))
        {
            if (_mountedVisual is not null)
                _root.Children.Remove(_mountedVisual);
            _root.Children.Add(view);
            _mountedVisual = view;
        }
        view.Visibility = Visibility.Visible;
        if (_web is not null)
            _web.Visibility = Visibility.Collapsed;
        if (Status != ViewerTabStatus.Running)
            SetStatus(ViewerTabStatus.Running);
    }

    private void HideVisual()
    {
        if (_mountedVisual is not null)
            _mountedVisual.Visibility = Visibility.Collapsed;
        if (_web is not null)
            _web.Visibility = Visibility.Visible;
    }

    private async Task<WebView2CompositionControl?> EnsureWebAsync()
    {
        if (_web is null)
        {
            _web = _factory.Create();
            _root.Children.Add(_web);
            _init = null;
            _mappedFolder = null;
        }
        var web = _web;
        _init ??= InitAsync(web);
        if (!await _init)
        {
            _init = null;
            return null;
        }
        return ReferenceEquals(_web, web) ? web : null;
    }

    private async Task<bool> InitAsync(WebView2CompositionControl web)
    {
        if (!await _factory.InitializeAsync(web) || web.TryCore() is not { } core)
            return false;
        try
        {
            core.SetVirtualHostNameToFolderMapping(MarkdownRenderer.AssetsVirtualHost,
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch { /* mermaid などが原文表示になるだけ */ }
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "linkClicked"
                    && root.TryGetProperty("href", out var href) && href.GetString() is { } link)
                    _ = _linkClicked(link, Path);
            }
            catch { /* 壊れたメッセージは捨てる */ }
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                Unmount();
                _stale = true;
                OnShownChanged();
            });
        };
        return true;
    }

    private void MapPreviewFolder(CoreWebView2 core, string? folder)
    {
        if (string.IsNullOrEmpty(folder) || string.Equals(folder, _mappedFolder, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            core.SetVirtualHostNameToFolderMapping(MarkdownRenderer.PreviewVirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            _mappedFolder = folder;
        }
        catch { /* 画像だけ出ない */ }
    }

    protected override void Unmount()
    {
        ++_renderSeq;
        _stale = true;
        if (_web is not null)
        {
            _root.Children.Remove(_web);
            _factory.Dispose(_web);
            _web = null;
            _init = null;
            _mappedFolder = null;
        }
        if (_mountedVisual is not null)
        {
            _root.Children.Remove(_mountedVisual);
            _mountedVisual = null;
        }
        _visuals?.Dispose();
        _visuals = null;
        Host.Children.Remove(_root);
    }

    public override void Dispose()
    {
        _fileWatcher.Dispose();
        base.Dispose();
    }
}
