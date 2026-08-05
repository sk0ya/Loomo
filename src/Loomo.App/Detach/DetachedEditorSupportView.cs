using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Editor.Controls;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Detach;

/// <summary>
/// EditorSupport（Markdown プレビュー等）の切り離し複製。追従元エディタの本文編集をデバウンスして
/// <b>専用の WebView2</b> へ、メイン表示と同じ <see cref="EditorSupportPipeline"/> で再描画する。
/// <para>
/// CSV グリッド・画像・Hex のようなビジュアル表示も<b>この複製で表示できる</b>。表示インスタンスは
/// 表示面ごとに作られる（<see cref="EditorSupportVisualHost"/>）ので、ペイン本体と同じ提供者を
/// 同時に使っても WPF の単一親制約に触れない。以前は提供者がビューを1つしか持てず、
/// この複製では「複製に未対応です」と出すしかなかった。
/// </para>
/// <para>
/// タブをウィンドウ間で移動すると WebView2 のコンポジションビジュアルが元ウィンドウのコンポジタに紐づいた
/// まま新ウィンドウへ移らず<b>空表示</b>になる。これを避けるため、再ペアレント（Unloaded→Loaded）を検出したら
/// WebView2 を作り直して再描画する。
/// </para>
/// </summary>
internal sealed class DetachedEditorSupportView : Grid, IDisposable
{
    internal string? SourceFilePath => _source.FilePath;
    private readonly EditorSupportResolver _resolver;
    private readonly EditorSupportPipeline _pipeline;
    private readonly IEditorSupportViewFactory _viewFactory;
    private readonly LoomoSettings _settings;
    /// <summary>相対パスの解決基準を描画のたびに問い直すためのワークスペース。切り離した時点の
    /// 基準を握らないこと——このビューはソースのエディタに追従するので、対象ファイルが別の
    /// ワークスペースフォルダーへ変わることがある。</summary>
    private readonly IWorkspaceService _workspace;
    private readonly VimEditorControl _source;
    private readonly DispatcherTimer _debounce;
    private readonly EditorSupportVisualHost _visuals;

    private WebView2CompositionControl? _web;
    private FrameworkElement? _mountedVisual;
    private Task<bool>? _initTask;
    private int _renderSeq;
    private CancellationTokenSource? _renderCts;
    private string? _mappedFolder;
    private bool _reattachPending;
    private bool _disposed;
    private string _searchTerm = "";
    private bool _searchCaseSensitive;
    private bool _searchUseRegex;

    /// <summary>タブ見出しに使う現在のプレビュー題名の変化通知。</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>プレビュー本文のリンククリック（href）。振り分けはメインウィンドウ側が行う。</summary>
    public event EventHandler<string>? LinkClicked;

    /// <summary>
    /// 右クリックしたリンク（生 href。リンク上でなければ null）に対する「別ウィンドウで開く」項目を
    /// ホストへ問い合わせる。宛先の解決（URL＝ブラウザ／ファイル＝エディタ）と切り離しウィンドウの生成は
    /// メインウィンドウ側の担当なので、この複製は見出しと実行を受け取ってメニューへ載せるだけにする。
    /// null を返せば項目を出さない。
    /// </summary>
    internal Func<string?, (string Header, Action Open)?>? LinkWindowMenu { get; set; }

    public DetachedEditorSupportView(
        EditorSupportResolver resolver, EditorSupportPipeline pipeline, IEditorSupportViewFactory viewFactory,
        LoomoSettings settings, IWorkspaceService workspace, VimEditorControl source)
    {
        _resolver = resolver;
        _visuals = new EditorSupportVisualHost(OnVisualContentEdited);
        _pipeline = pipeline;
        _viewFactory = viewFactory;
        _settings = settings;
        _workspace = workspace;
        _source = source;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RenderAsync(); };

        _source.BufferChanged += OnSourceChanged;
        Loaded += OnLoaded;
        // 別ウィンドウへ移されると Unloaded→Loaded が発火する。次の Loaded で WebView2 を作り直す。
        Unloaded += (_, _) => _reattachPending = true;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_web is null || _reattachPending)
            RebuildWebView();
        _reattachPending = false;
        _ = RenderAsync();
    }

    /// <summary>プレビュー内で塗る検索ワードを設定する（空で消える）。メイン側の EditorSupport と同じ条件を
    /// ShellWindow が配る。条件は保持しておき、再描画（ナビゲーション完了）のたびに送り直す。</summary>
    internal void SetSearchHighlight(string? term, bool caseSensitive, bool useRegex)
    {
        _searchTerm = term ?? "";
        _searchCaseSensitive = caseSensitive;
        _searchUseRegex = useRegex;
        if (_web?.CoreWebView2 is { } core)
            EditorSupportSearchHighlight.Post(core, _searchTerm, _searchCaseSensitive, _searchUseRegex);
        _visuals.SetSearchHighlight(_searchTerm, _searchCaseSensitive, _searchUseRegex);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>WebView2 を（作り直して）生成する。再ペアレント後に新ウィンドウのコンポジタへ確実に載せる。</summary>
    private void RebuildWebView()
    {
        if (_web is not null)
        {
            Children.Remove(_web);
            _viewFactory.Dispose(_web);
        }
        _web = _viewFactory.Create();
        Children.Add(_web);
        _initTask = null;
        _mappedFolder = null;
    }

    private async Task RenderAsync()
    {
        var seq = ++_renderSeq;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        var cts = _renderCts = new CancellationTokenSource();
        var ct = cts.Token;

        var theme = _settings.Appearance.MarkdownPreviewTheme;
        var filePath = _source.FilePath;
        // 解決は本体と同じ Resolver を通す（Registry を直に引くと Hex/コードのフォールバックが抜ける）。
        var selection = string.IsNullOrEmpty(filePath) ? null : _resolver.Resolve(filePath);

        // ビジュアル表示（CSV グリッド・画像・Hex）は WebView2 を使わないので先に分岐する。
        if (selection?.Provider is IEditorSupportVisualProvider visualProvider && filePath is not null)
        {
            try
            {
                var visual = _visuals.GetOrCreate(visualProvider);
                var apply = await visual.PrepareAsync(
                    filePath, visualProvider.UsesEditorText ? _source.Text : string.Empty, ct);
                if (seq != _renderSeq)
                    return;
                TitleChanged?.Invoke(this, visualProvider.DescribeTitle(filePath));
                MountVisual(visual.View);
                apply();
            }
            catch (OperationCanceledException) { }
            catch { /* 表示できなければ前の表示のまま */ }
            return;
        }

        HideVisual();
        var view = await EnsureWebAsync();
        if (view?.CoreWebView2 is not { } core || seq != _renderSeq)
            return;

        if (string.IsNullOrEmpty(filePath))
        {
            Navigate(core, MarkdownRenderer.RenderToHtml(
                "## Editor Support\n\n表示するファイルがありません。", "Editor Support", theme));
            return;
        }

        var result = await _pipeline.PrepareAsync(selection?.Provider, EditorSupportContext.For(
            _workspace, filePath, _source.Text, null, theme));
        if (seq != _renderSeq)
            return;

        TitleChanged?.Invoke(this, result.Title);
        if (result.Uri is { } uri)
        {
            try { core.Navigate(uri); }
            catch { /* 無効 URI は無視 */ }
            return;
        }

        UpdatePreviewHost(core, result.MapFolder);
        if (result.Html is { } html)
            Navigate(core, html);
    }

    /// <summary>ビジュアル表示を載せて WebView2 を隠す（複製ウィンドウ側の表示切替）。</summary>
    private void MountVisual(FrameworkElement view)
    {
        if (!ReferenceEquals(_mountedVisual, view))
        {
            if (_mountedVisual is not null)
                Children.Remove(_mountedVisual);
            Children.Add(view);
            _mountedVisual = view;
        }
        view.Visibility = Visibility.Visible;
        if (_web is not null)
            _web.Visibility = Visibility.Collapsed;
    }

    private void HideVisual()
    {
        if (_mountedVisual is not null)
            _mountedVisual.Visibility = Visibility.Collapsed;
        if (_web is not null)
            _web.Visibility = Visibility.Visible;
    }

    /// <summary>複製側のグリッド編集も追従元エディタへ書き戻す（本体ペインと同じ扱い）。</summary>
    private void OnVisualContentEdited(object? sender, EditorSupportContentEdited e)
    {
        if (!string.Equals(_source.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase))
            return;
        if (_source.Text == e.Text)
            return;
        _source.SetText(e.Text);
    }

    private static void Navigate(CoreWebView2 core, string html)
    {
        // NavigateToString は約 2MB 上限。大きなプレビューは表示されないことがある（既知の制限）。
        try { core.NavigateToString(html); }
        catch { /* 上限超過等は前回表示のまま */ }
    }

    private async Task<WebView2CompositionControl?> EnsureWebAsync()
    {
        if (_web is null)
            return null;
        _initTask ??= InitCoreAsync(_web);
        if (!await _initTask)
        {
            _initTask = null;
            return null;
        }
        return _web;
    }

    private async Task<bool> InitCoreAsync(WebView2CompositionControl web)
    {
        if (!await _viewFactory.InitializeAsync(web))
            return false;
        if (web.CoreWebView2 is not { } core)
            return false;

        // 同梱アセット（mermaid 等）の配信元を一度だけマップする。
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                MarkdownRenderer.AssetsVirtualHost,
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Web"),
                CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch { /* 失敗しても mermaid が原文表示になるだけ */ }

        // 検索ハイライト（メイン側の EditorSupport と同じ仕込み）。ページを組み直すたびに条件は消えるので、
        // ナビゲーション完了で送り直す。
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(EditorSupportSearchHighlight.Script); }
        catch { /* 失敗しても塗られないだけ */ }

        // 右クリック位置のリンク（生 href）を拾う仕込みと、それを使う「別ウィンドウで開く」項目。
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(EditorSupportContextLink.Script); }
        catch { /* 失敗しても項目が出ないだけ */ }
        core.ContextMenuRequested += OnContextMenuRequested;
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
                EditorSupportSearchHighlight.Post(core, _searchTerm, _searchCaseSensitive, _searchUseRegex);
        };

        // ページ側スクリプトからのメッセージ（リンククリック等）を受ける。WebView2 は再ペアレント時に
        // 作り直される（RebuildWebView）が、その都度この初期化を通るので購読も新しい core に張り直る。
        core.WebMessageReceived += OnWebMessageReceived;
        return true;
    }

    /// <summary>
    /// プレビュー本文のリンククリックをホストへ中継する。スクロール同期・タスクチェックボックス等の
    /// 他メッセージは、この複製が追従専用（メインのパイプラインに触れない）なので扱わない。
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "linkClicked"
                && root.TryGetProperty("href", out var hrefElement)
                && hrefElement.GetString() is { } href)
            {
                LinkClicked?.Invoke(this, href);
            }
        }
        catch { /* 壊れたメッセージは無視 */ }
    }

    /// <summary>リンク上での右クリックか（＝生 href の取得）は非同期でしか分からないので、
    /// deferral でメニュー表示を待たせてから項目を足す。</summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core || LinkWindowMenu is null)
            return;
        EditorSupportContextLink.RemoveBuiltInOpenInNewWindow(e.MenuItems);
        _ = AddLinkMenuItemAsync(core, e, e.GetDeferral());
    }

    private async Task AddLinkMenuItemAsync(
        CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e, CoreWebView2Deferral deferral)
    {
        try
        {
            var href = await EditorSupportContextLink.ReadHrefAsync(core);
            if (LinkWindowMenu?.Invoke(href) is not { } menu)
                return;
            var item = core.Environment.CreateContextMenuItem(
                menu.Header, null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(menu.Open);
            e.MenuItems.Insert(0, item);
        }
        catch { /* 項目を出せなくても既定のメニューは出す */ }
        finally { deferral.Complete(); }
    }

    /// <summary>プレビューの相対パス画像用に、preview 仮想ホストを表示中ファイルのフォルダへ張り替える。</summary>
    private void UpdatePreviewHost(CoreWebView2 core, string? folder)
    {
        if (string.IsNullOrEmpty(folder)
            || string.Equals(folder, _mappedFolder, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                MarkdownRenderer.PreviewVirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            _mappedFolder = folder;
        }
        catch { /* 画像だけ出ない */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.BufferChanged -= OnSourceChanged;
        _debounce.Stop();
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        _visuals.Dispose();
        _mountedVisual = null;
        if (_web is not null)
        {
            _viewFactory.Dispose(_web);
            _web = null;
        }
    }
}
