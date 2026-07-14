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

namespace sk0ya.Loomo.App.Detach;

/// <summary>
/// EditorSupport（Markdown プレビュー等）の切り離し複製。追従元エディタの本文編集をデバウンスして
/// <b>専用の WebView2</b> へ再描画する（メインの EditorSupport パイプラインには触れない＝多重化リスク回避）。
/// プロバイダ層（<see cref="EditorSupportRegistry"/> / <see cref="IEditorSupportHtmlProvider"/> /
/// <see cref="IEditorSupportUriProvider"/>）だけを再利用する。CSV グリッド等のビジュアル提供者・コード構造
/// アウトラインは共有ビューが単一親制約に反するため、この複製では案内表示にとどめる（既知の制限）。
/// <para>
/// タブをウィンドウ間で移動すると WebView2 のコンポジションビジュアルが元ウィンドウのコンポジタに紐づいた
/// まま新ウィンドウへ移らず<b>空表示</b>になる。これを避けるため、再ペアレント（Unloaded→Loaded）を検出したら
/// WebView2 を作り直して再描画する。
/// </para>
/// </summary>
internal sealed class DetachedEditorSupportView : Grid, IDisposable
{
    internal string? SourceFilePath => _source.FilePath;
    private readonly EditorSupportRegistry _editorSupports;
    private readonly AiSettings _settings;
    private readonly string? _workspaceRoot;
    private readonly VimEditorControl _source;
    private readonly DispatcherTimer _debounce;

    private WebView2CompositionControl? _web;
    private Task<bool>? _initTask;
    private int _renderSeq;
    private string? _mappedFolder;
    private bool _reattachPending;
    private bool _disposed;

    /// <summary>タブ見出しに使う現在のプレビュー題名の変化通知。</summary>
    public event EventHandler<string>? TitleChanged;

    public DetachedEditorSupportView(
        EditorSupportRegistry editorSupports, AiSettings settings, string? workspaceRoot, VimEditorControl source)
    {
        _editorSupports = editorSupports;
        _settings = settings;
        _workspaceRoot = workspaceRoot;
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
            try { _web.Dispose(); } catch { /* 破棄失敗は無視 */ }
        }
        _web = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
        };
        Children.Add(_web);
        _initTask = null;
        _mappedFolder = null;
    }

    private async Task RenderAsync()
    {
        var seq = ++_renderSeq;
        var view = await EnsureWebAsync();
        if (view?.CoreWebView2 is not { } core || seq != _renderSeq)
            return;

        var theme = _settings.Appearance.MarkdownPreviewTheme;
        var filePath = _source.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            Navigate(core, MarkdownRenderer.RenderToHtml(
                "## Editor Support\n\n表示するファイルがありません。", "Editor Support", theme));
            return;
        }

        var provider = _editorSupports.Resolve(filePath);

        if (provider is IEditorSupportUriProvider uriProvider)
        {
            TitleChanged?.Invoke(this, uriProvider.DescribeTitle(filePath));
            try { core.Navigate(uriProvider.ResolveNavigationUri(filePath)); }
            catch { /* 無効 URI は無視 */ }
            return;
        }

        if (provider is IEditorSupportHtmlProvider htmlProvider)
        {
            var text = provider.UsesEditorText ? _source.Text : string.Empty;
            string html;
            try
            {
                html = await Task.Run(() => htmlProvider.RenderHtml(filePath, text));
            }
            catch (Exception ex)
            {
                html = MarkdownRenderer.RenderToHtml(
                    $"## プレビューエラー\n\n```\n{ex}\n```", "Preview Error", theme);
            }
            if (seq != _renderSeq)
                return;

            TitleChanged?.Invoke(this, htmlProvider.DescribeTitle(filePath));
            UpdatePreviewHost(core, MarkdownPreviewPaths.Resolve(_workspaceRoot, filePath).MapFolder);
            Navigate(core, html);
            return;
        }

        // ビジュアル提供者（CSV グリッド等）・コード構造・非対応ファイルは案内にとどめる。
        TitleChanged?.Invoke(this, "Editor Support");
        Navigate(core, MarkdownRenderer.RenderToHtml(
            "## Editor Support\n\nこの種類のプレビューは別ウィンドウでの複製に未対応です。",
            "Editor Support", theme));
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
        try { await web.EnsureCoreWebView2Async(); }
        catch { return false; }
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
        return true;
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
        if (_web is not null)
        {
            try { _web.Dispose(); } catch { /* 破棄失敗は無視 */ }
            _web = null;
        }
    }
}
