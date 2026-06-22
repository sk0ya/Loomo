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
/// <summary>ShellWindow: EditorSupport ペイン（Markdown プレビュー等の表示・スクロール同期）。
/// 自動表示はしない（明示操作で開いたときだけアクティブエディタに追従して描く）。</summary>
public partial class ShellWindow
{
    /// <summary>
    /// エディタからの明示プレビュー要求：EditorSupport ペインを開いて内容を流し込む。
    /// タイル表示なら Editor の右隣へ開き、ソロモードなら舞台へ立てる。
    /// </summary>
    private async Task OpenEditorSupportAsync(EditorTab sourceTab)
    {
        await SwitchEditorSupportSourceAsync(sourceTab, force: true);
        if (_stageActive)
            SetStagePane(PaneKind.EditorSupport);   // ソロは舞台へ立てる
        else
            ShowEditorSupportPane();                 // タイルは Editor の右隣へ開く
        await UpdateEditorSupportAsync();
    }

    /// <summary>EditorSupport の追従先エディタタブを切り替えて内容を更新する（同一タブなら何もしない）。</summary>
    private async Task SwitchEditorSupportSourceAsync(EditorTab sourceTab, bool force = false)
    {
        if (ReferenceEquals(_editorSupportSourceTab, sourceTab))
            return;
        if (_editorSupportSourcePinned && !force && _editorSupportSourceTab is not null)
            return;

        if (_editorSupportSourceTab is not null)
            _editorSupportSourceTab.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;

        _editorSupportSourceTab = sourceTab;
        sourceTab.Control.ViewportScrolled += EditorSupportSource_ViewportScrolled;
        UpdateEditorSupportPinToggle();

        await UpdateEditorSupportAsync();
    }

    /// <summary>EditorSupport ヘッダーのピン：追従先タブを現在の対象へ固定／固定解除する。</summary>
    private async void OnToggleEditorSupportPin(object sender, RoutedEventArgs e)
    {
        _editorSupportSourcePinned = EditorSupportPinToggle.IsChecked == true;
        UpdateEditorSupportPinToggle();

        if (_editorSupportSourcePinned)
        {
            if (_editorSupportSourceTab is null && _activeEditorTab is not null)
                await SwitchEditorSupportSourceAsync(_activeEditorTab, force: true);
            return;
        }

        if (_activeEditorTab is not null)
            await SwitchEditorSupportSourceAsync(_activeEditorTab, force: true);
    }

    private void UpdateEditorSupportPinToggle()
    {
        EditorSupportPinToggle.IsChecked = _editorSupportSourcePinned;
        EditorSupportPinToggle.ToolTip = _editorSupportSourcePinned
            ? "ピン留めを解除してアクティブなエディタに追従"
            : "現在のサポート対象にピン留め";
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
    /// 追従先エディタの内容を EditorSupport ペインへ反映する。ペインの開閉はしない（明示操作のみ）。
    /// ペインが表示されている（タイルで可視 or ソロで舞台）ときだけ中身を描く。
    /// </summary>
    private async Task UpdateEditorSupportAsync()
    {
        var source = _editorSupportSourceTab;
        if (source is null)
            return;

        var filePath = source.Control.FilePath;
        var provider = _editorSupports.Resolve(filePath);

        // 自動表示はしない。ペインが実際に表示されている（タイルで可視 or ソロで舞台）ときだけ描く。
        // 判定は EditorSupportRenderPolicy に一元化（テスト可能）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(onStage, IsPaneVisible(PaneKind.EditorSupport)))
            return;

        // WPF コントロールをそのまま表示する提供者（CSV/TSV グリッド等）。WebView2 は使わない。
        if (provider is IEditorSupportVisualProvider visual && filePath is not null)
        {
            if (_editorSupportEditSubscribed.Add(visual))
                visual.ContentEdited += EditorSupportVisual_ContentEdited;

            ShowEditorSupportVisual(visual.GetOrCreateView());
            EditorSupportTitle.Text = visual.DescribeTitle(filePath);
            await visual.UpdateAsync(filePath, source.Control.Text);
            return;
        }

        // WebView2 系。直前までビジュアル系を表示していたら退ける。
        HideEditorSupportVisual();

        // 描画内容は init を待つ前に確定し、最新の要求として登録する。WebView2 の初回初期化は
        // 起動時だけ数秒かかり、その間に複数の更新が積み重なるため、シーケンス番号で最後の1つへ畳む。
        string title;
        string? html = null;
        string? uri = null;
        string? mapFolder = null;
        if (provider is IEditorSupportUriProvider uriProvider && filePath is not null)
        {
            // PDF・SVG・HTML 等はファイルをそのままブラウザへナビゲートする（本文には依存しない）。
            title = uriProvider.DescribeTitle(filePath);
            uri = uriProvider.ResolveNavigationUri(filePath);
        }
        else if (provider is IEditorSupportHtmlProvider htmlProvider && filePath is not null)
        {
            title = htmlProvider.DescribeTitle(filePath);
            html = htmlProvider.RenderHtml(filePath, source.Control.Text);
            mapFolder = MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder;
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

        _editorSupportPendingHtml = html;
        _editorSupportPendingUri = uri;
        _editorSupportPendingMapFolder = mapFolder;
        EditorSupportTitle.Text = title;
        var seq = ++_editorSupportRenderSeq;

        var view = await EnsureEditorSupportViewAsync();
        if (view?.CoreWebView2 is null)
            return;

        // init を待っている間に新しい描画要求が来ていれば、そちらが描くのでこのコールは降りる
        // （起動時に殺到した要求が同時に NavigateToString して初回ナビゲーションを潰し合うのを防ぐ）。
        if (seq != _editorSupportRenderSeq)
            return;

        RenderPendingEditorSupportContent(view.CoreWebView2);
    }

    /// <summary>
    /// 最新の描画内容を WebView2 へ反映する。URI プロバイダ（PDF 等）はファイルへ直接ナビゲートし、
    /// それ以外（Markdown プレビュー等）は HTML 文字列をナビゲートする。
    /// </summary>
    private void RenderPendingEditorSupportContent(CoreWebView2 core)
    {
        if (_editorSupportPendingUri is { } uri)
        {
            // 同一ファイルへの再ナビゲート（本文編集のデバウンス等）は PDF のスクロール位置を失うので避ける。
            if (string.Equals(uri, _editorSupportNavigatedUri, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                core.Navigate(uri);
                _editorSupportNavigatedUri = uri;
            }
            catch
            {
                // 無効な URI 等で失敗しても落とさない（表示は前回内容のまま）。
            }
            return;
        }

        if (_editorSupportPendingHtml is null)
            return;

        // HTML を描いたら、次に同じ URI へ戻ったとき確実に再ナビゲートできるようガードを解除する。
        _editorSupportNavigatedUri = null;

        if (_editorSupportPendingMapFolder is not null)
            UpdateEditorSupportVirtualHost(core, _editorSupportPendingMapFolder);

        try
        {
            core.NavigateToString(_editorSupportPendingHtml);
        }
        catch
        {
            // NavigateToString の上限（約2MB）超過などで失敗しても落とさない（プレビューは前回内容のまま）。
        }
    }

    /// <summary>ビジュアル提供者のビューをペインへ載せ、WebView2 を隠す（差し替え時は古いビューを外す）。</summary>
    private void ShowEditorSupportVisual(FrameworkElement view)
    {
        if (!ReferenceEquals(_editorSupportVisual, view))
        {
            if (_editorSupportVisual is not null)
                EditorSupportContentHost.Children.Remove(_editorSupportVisual);
            EditorSupportContentHost.Children.Add(view);
            _editorSupportVisual = view;
        }

        view.Visibility = Visibility.Visible;
        if (_editorSupportView is not null)
            _editorSupportView.Visibility = Visibility.Collapsed;
    }

    /// <summary>ビジュアル提供者のビューを隠し、WebView2 表示へ戻す。</summary>
    private void HideEditorSupportVisual()
    {
        if (_editorSupportVisual is not null)
            _editorSupportVisual.Visibility = Visibility.Collapsed;
        if (_editorSupportView is not null)
            _editorSupportView.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// ビジュアル提供者内での編集（CSV/TSV グリッド等）を、追従中のエディタタブの本文へ書き戻す。
    /// SetText で BufferChanged が発火しデバウンス更新が走るが、提供者側が内容比較で再パースを
    /// 抑止するためループしない。エディタタブは通常の編集と同じく未保存（modified）になる。
    /// </summary>
    private void EditorSupportVisual_ContentEdited(object? sender, EditorSupportContentEdited e)
    {
        var tab = _editorSupportSourceTab;
        if (tab is null
            || !string.Equals(tab.Control.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (tab.Control.Text == e.Text)
            return;

        tab.Control.SetText(e.Text);
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

    /// <summary>EditorSupport ペインを（無ければ Editor の右隣へ作って）表示する。明示プレビュー要求用。</summary>
    private void ShowEditorSupportPane()
    {
        if (IsPaneVisible(PaneKind.EditorSupport))
            return;

        EnsureEditorSupportLeafBesideEditor();
        SetPaneVisible(PaneKind.EditorSupport, true);
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
            _editorSupportView.NavigationCompleted += OnEditorSupportNavigationCompleted;
            EditorSupportContentHost.Children.Add(_editorSupportView);
        }

        // 初期化は1回だけ。起動時に殺到する複数の描画要求が同じ初期化 Task を待つことで、
        // EnsureCoreWebView2Async の多重呼び出し（＝初回ナビゲーションのレース）を防ぐ。
        _editorSupportInitTask ??= InitializeEditorSupportCoreAsync(_editorSupportView);
        if (!await _editorSupportInitTask)
        {
            _editorSupportInitTask = null; // 失敗時は次回やり直せるようにする
            return null;
        }

        return _editorSupportView;
    }

    /// <summary>CoreWebView2 を実体化し、Web イベントと同梱アセットのホストマップを一度だけ設定する。</summary>
    private async Task<bool> InitializeEditorSupportCoreAsync(WebView2CompositionControl view)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
        }
        catch
        {
            return false;
        }

        if (view.CoreWebView2 is null)
            return false;

        if (!_editorSupportWebEventsAttached)
        {
            view.CoreWebView2.WebMessageReceived += EditorSupport_WebMessageReceived;

            // 同梱 Web アセット（mermaid.min.js）の配信元。アプリ出力フォルダ固定なので一度だけマップする。
            try
            {
                view.CoreWebView2.SetVirtualHostNameToFolderMapping(
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

        return true;
    }

    private void OnEditorSupportNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // 起動直後だけ、生成直後の WebView2 が初回ナビゲーションを取りこぼすことがある。最初の完了時に
        // 最新内容を一度だけ描き直して自己修復する（描き直しの完了は latch 済みなので再帰しない）。
        if (!_editorSupportFirstRenderHealed && _editorSupportView?.CoreWebView2 is { } core)
        {
            _editorSupportFirstRenderHealed = true;
            RenderPendingEditorSupportContent(core);
        }

        // コンテンツの描画が終わったら、エディタの現在位置までスクロールを合わせ直す。
        if (_editorSupportSourceTab is not null)
            PostEditorSupportScrollRatio(_editorSupportSourceTab.Control.VerticalScrollRatio);
    }

    private void DetachEditorSupportSource()
    {
        if (_editorSupportSourceTab is not null)
            _editorSupportSourceTab.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
        _editorSupportSourceTab = null;
    }

    private void EditorSupportSource_ViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingEditorFromSupport || sender is not VimEditorControl editor)
            return;

        PostEditorSupportScrollRatio(editor.VerticalScrollRatio);
    }

    private void EditorSupport_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_editorSupportSourceTab is null)
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

    /// <summary>
    /// エディタの縦スクロール位置（比率）をプレビューへ送る。<c>ExecuteScriptAsync</c>（スクリプト文字列の
    /// 都度コンパイル＋IPC 往復待ち）ではなく <see cref="CoreWebView2.PostWebMessageAsJson"/> を使う
    /// ＝送りっぱなしで安く、連続スクロールでも待ち行列が詰まらない。間引き（1 フレーム 1 回の scrollTo）は
    /// ページ側の requestAnimationFrame が担う。エコー抑止もページ側 suppressScrollMessage が担う。
    /// </summary>
    private void PostEditorSupportScrollRatio(double ratio)
    {
        var core = _editorSupportView?.CoreWebView2;
        if (core is null)
            return;

        try
        {
            core.PostWebMessageAsJson(FormattableString.Invariant(
                $"{{\"type\":\"setScrollRatio\",\"ratio\":{Math.Clamp(ratio, 0.0, 1.0):R}}}"));
        }
        catch
        {
            // Best effort: the preview can be navigating while the editor scrolls.
        }
    }
}
