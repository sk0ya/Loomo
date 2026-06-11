using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: EditorSupport ペイン（Markdown プレビュー等の自動表示・スクロール同期）</summary>
public partial class ShellWindow
{
    /// <summary>エディタからの明示プレビュー要求：EditorSupport ペインを手動表示扱いで開き、内容を流し込む。</summary>
    private async Task OpenEditorSupportAsync(EditorTab sourceTab)
    {
        _editorSupportUserVisibility = true;
        await SwitchEditorSupportSourceAsync(sourceTab);
        await UpdateEditorSupportAsync();
    }

    /// <summary>EditorSupport の追従先エディタタブを切り替えて内容を更新する（同一タブなら何もしない）。</summary>
    private async Task SwitchEditorSupportSourceAsync(EditorTab sourceTab)
    {
        if (ReferenceEquals(_editorSupportSourceTab, sourceTab))
            return;

        if (_editorSupportSourceTab is not null)
            _editorSupportSourceTab.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;

        _editorSupportSourceTab = sourceTab;
        sourceTab.Control.ViewportScrolled += EditorSupportSource_ViewportScrolled;

        await UpdateEditorSupportAsync();
    }

    /// <summary>編集中の連続更新をまとめる（300ms デバウンスで <see cref="UpdateEditorSupportAsync"/>）。</summary>
    private void ScheduleEditorSupportUpdate()
    {
        if (_editorSupportSourceTab is null)
            return;

        if (_editorSupportDebounceTimer is null)
        {
            _editorSupportDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _editorSupportDebounceTimer.Tick += async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await UpdateEditorSupportAsync();
            };
        }

        _editorSupportDebounceTimer.Stop();
        _editorSupportDebounceTimer.Start();
    }

    /// <summary>
    /// 追従先エディタの内容を EditorSupport ペインへ反映する。ファイルに対応する
    /// <see cref="IEditorSupportProvider"/> が無ければペインを自動で閉じ、あれば自動で開く
    /// （ユーザーのトグル操作 <see cref="_editorSupportUserVisibility"/> が最優先）。
    /// </summary>
    private async Task UpdateEditorSupportAsync()
    {
        var source = _editorSupportSourceTab;
        if (source is null)
            return;

        var filePath = source.Control.FilePath;
        var provider = _editorSupports.Resolve(filePath);

        var shouldShow = _editorSupportUserVisibility ?? provider is not null;
        SetEditorSupportVisibleAuto(shouldShow);
        if (!shouldShow || !IsPaneVisible(PaneKind.EditorSupport))
            return;

        var view = await EnsureEditorSupportViewAsync();
        if (view?.CoreWebView2 is null)
            return;

        string title;
        string html;
        if (provider is not null && filePath is not null)
        {
            title = provider.DescribeTitle(filePath);
            html = provider.RenderHtml(filePath, source.Control.Text);
            UpdateEditorSupportVirtualHost(
                view.CoreWebView2, MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder);
        }
        else
        {
            // 手動表示中で対応プロバイダの無いファイル：案内だけ出す。
            title = "Editor Support";
            html = MarkdownRenderer.RenderToHtml(
                "## Editor Support\n\nこのファイルに対応するサポートはありません。",
                title,
                _settings.Appearance.MarkdownPreviewTheme);
        }

        view.CoreWebView2.NavigateToString(html);
        EditorSupportTitle.Text = title;
    }

    /// <summary>
    /// プレビューの相対パス画像（&lt;base href&gt; = <see cref="MarkdownRenderer.PreviewVirtualHost"/>）を
    /// 表示中ファイルのフォルダから読めるよう、仮想ホストのマップ先を切り替える。
    /// NavigateToString のページは about:blank オリジンのため file:// は読めず、このマップが必要。
    /// </summary>
    private void UpdateEditorSupportVirtualHost(CoreWebView2 core, string? folder)
    {
        if (string.IsNullOrEmpty(folder)
            || string.Equals(folder, _editorSupportMappedFolder, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // 同名ホストへの再呼び出しはマップ先の差し替えになる。DenyCors でも <img> は読める。
            core.SetVirtualHostNameToFolderMapping(
                MarkdownRenderer.PreviewVirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            _editorSupportMappedFolder = folder;
        }
        catch
        {
            // マップ失敗（無効なフォルダ等）でもプレビュー本文の表示は続ける（画像だけ出ない）。
        }
    }

    /// <summary>EditorSupport ペインの自動開閉（ユーザー操作と区別するためガードを立てて呼ぶ）。</summary>
    private void SetEditorSupportVisibleAuto(bool visible)
    {
        if (IsPaneVisible(PaneKind.EditorSupport) == visible)
            return;

        if (visible)
            EnsureEditorSupportLeafBesideEditor();

        _editorSupportAutoToggling = true;
        try
        {
            SetPaneVisible(PaneKind.EditorSupport, visible);
        }
        finally
        {
            _editorSupportAutoToggling = false;
        }
    }

    /// <summary>
    /// EditorSupport リーフがレイアウトツリーに無ければ Editor の右隣へ（隠した状態で）挿入する。
    /// 既定の <see cref="AddLeafAtBottom"/>（最下段の新しい行）よりプレビュー用途に適した位置になる。
    /// </summary>
    private void EnsureEditorSupportLeafBesideEditor()
    {
        // 跨ぎ最大化中は、解除時に戻す保存レイアウトにも同じ位置（Editor の右隣・隠した状態）で
        // 確保しておく（解除後の再表示位置が最下段の全幅行に落ちないように）。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot
            && AllLeaves(savedRoot).All(l => l.Kind != PaneKind.EditorSupport)
            && AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == PaneKind.Editor) is { } savedEditor)
        {
            _spanSavedRoot = InsertRelative(
                savedRoot, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, savedEditor, DropZone.Right);
        }

        if (FindLeaf(PaneKind.EditorSupport) is not null)
            return;
        if (FindLeaf(PaneKind.Editor) is not { } editorLeaf)
            return; // Editor がツリーに無い場合は SetPaneVisible の既定動作（最下段へ追加）に任せる

        CaptureLayoutSizes();
        _root = InsertRelative(_root, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, editorLeaf, DropZone.Right);
    }

    /// <summary>EditorSupport ペインの WebView2 を遅延生成し、CoreWebView2 まで実体化して返す（失敗時 null）。</summary>
    private async Task<WebView2CompositionControl?> EnsureEditorSupportViewAsync()
    {
        if (_editorSupportView is null)
        {
            _editorSupportView = new WebView2CompositionControl
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
                CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = WebViewUserDataFolder }
            };
            // コンテンツの描画が終わったら、エディタの現在位置までスクロールを合わせ直す。
            _editorSupportView.NavigationCompleted += (_, _) =>
            {
                if (_editorSupportSourceTab is not null)
                    _ = QueueEditorSupportScrollSyncAsync(_editorSupportSourceTab.Control.VerticalScrollRatio);
            };
            EditorSupportContentHost.Children.Add(_editorSupportView);
        }

        try
        {
            await _editorSupportView.EnsureCoreWebView2Async();
        }
        catch
        {
            return null;
        }

        if (!_editorSupportWebEventsAttached && _editorSupportView.CoreWebView2 is not null)
        {
            _editorSupportView.CoreWebView2.WebMessageReceived += EditorSupport_WebMessageReceived;

            // 同梱 Web アセット（mermaid.min.js）の配信元。アプリ出力フォルダ固定なので一度だけマップする。
            try
            {
                _editorSupportView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    MarkdownRenderer.AssetsVirtualHost,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Web"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch
            {
                // マップ失敗時は mermaid 図が原文表示になるだけで、プレビュー自体は動く。
            }

            _editorSupportWebEventsAttached = true;
        }

        return _editorSupportView;
    }

    private void DetachEditorSupportSource()
    {
        if (_editorSupportSourceTab is not null)
            _editorSupportSourceTab.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
        _editorSupportSourceTab = null;
    }

    private async void EditorSupportSource_ViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingEditorFromSupport || sender is not VimEditorControl editor)
            return;

        await QueueEditorSupportScrollSyncAsync(editor.VerticalScrollRatio);
    }

    private void EditorSupport_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_syncingSupportFromEditor || _editorSupportSourceTab is null)
            return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "markdownPreviewScroll"
                || !root.TryGetProperty("ratio", out var ratioElement)
                || !ratioElement.TryGetDouble(out var ratio))
                return;

            _syncingEditorFromSupport = true;
            _editorSupportSourceTab.Control.ScrollToVerticalRatio(ratio);
        }
        catch
        {
            // Ignore malformed messages from preview content.
        }
        finally
        {
            _syncingEditorFromSupport = false;
        }
    }

    private async Task QueueEditorSupportScrollSyncAsync(double ratio)
    {
        _pendingEditorSupportScrollRatio = Math.Clamp(ratio, 0.0, 1.0);
        if (_editorSupportScrollSyncQueued)
            return;

        _editorSupportScrollSyncQueued = true;
        try
        {
            while (_editorSupportView is not null)
            {
                var nextRatio = _pendingEditorSupportScrollRatio;
                await ScrollEditorSupportToRatioAsync(nextRatio);

                if (Math.Abs(nextRatio - _pendingEditorSupportScrollRatio) < 0.0001)
                    break;
            }
        }
        finally
        {
            _editorSupportScrollSyncQueued = false;
        }
    }

    private async Task ScrollEditorSupportToRatioAsync(double ratio)
    {
        var view = _editorSupportView;
        if (view?.CoreWebView2 is null)
            return;

        _syncingSupportFromEditor = true;
        try
        {
            var script = FormattableString.Invariant(
                $"window.setMarkdownPreviewScrollRatio && window.setMarkdownPreviewScrollRatio({Math.Clamp(ratio, 0.0, 1.0):R});");
            await view.ExecuteScriptAsync(script);
        }
        catch
        {
            // Best effort: the preview can be navigating while the editor scrolls.
        }
        finally
        {
            _syncingSupportFromEditor = false;
        }
    }
}
