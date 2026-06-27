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

    /// <summary>
    /// EditorSupport ヘッダーの発表トグル：プレビューを発表モード（1枚ずつ）／縦並び全表示で切り替える。
    /// 共有 <see cref="AiSettings"/> の値を更新すると provider が次の描画から反映する。既定（OFF）は全スライド
    /// の縦並び表示。marp:true 文書は常に marp で描かれ、このトグルで発表⇔縦並びを切り替える。
    /// </summary>
    private async void OnToggleEditorSupportSlideMode(object sender, RoutedEventArgs e)
    {
        _settings.Appearance.MarkdownSlideMode = EditorSupportSlideToggle.IsChecked == true;
        await UpdateEditorSupportAsync();
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

        // 自動表示はしない。ペインが実際に表示されている
        // （タイルで可視 / ソロで舞台 / 袖・俯瞰ミニチュア）ときだけ描く。
        // 判定は EditorSupportRenderPolicy に一元化（テスト可能）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(
                onStage,
                IsPaneVisible(PaneKind.EditorSupport),
                IsEditorSupportInThumbnail()))
            return;

        // WPF コントロールをそのまま表示する提供者（CSV/TSV グリッド等）。WebView2 は使わない。
        // 対応プロバイダが無く、かつバイナリのファイルは Hex ダンプへフォールバックする（registry 外）。
        var visual = provider as IEditorSupportVisualProvider;
        if (visual is null && provider is null && filePath is not null && BinaryFileDetector.IsBinary(filePath))
            visual = _hexSupport;

        if (visual is not null && filePath is not null)
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

        // 本文スナップショットは UI スレッドで取る（エディタは UI スレッド専有）。重い
        // Markdown→HTML 変換はこの後バックグラウンドで行うので、ここで一度だけ読む。
        var text = source.Control.Text;

        // 描画要求のシーケンス番号。init / 変換の await を跨いで最後の要求だけが描くよう畳む。
        var seq = ++_editorSupportRenderSeq;

        string title;
        string? html = null;
        string? body = null;
        string? uri = null;
        string? mapFolder = null;
        string? pageKey = null;
        if (provider is IEditorSupportUriProvider uriProvider && filePath is not null)
        {
            // PDF・SVG・HTML 等はファイルをそのままブラウザへナビゲートする（本文には依存しない）。
            title = uriProvider.DescribeTitle(filePath);
            uri = uriProvider.ResolveNavigationUri(filePath);
        }
        else if (provider is IEditorSupportHtmlProvider htmlProvider && filePath is not null)
        {
            title = htmlProvider.DescribeTitle(filePath);
            mapFolder = MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder;

            // 同一ページ（テーマ・base href・対象ファイルが不変）を編集中なら、本文だけ差し替えて
            // フル再ナビゲート（＝ページ再構築のチカチカ）を避ける。鍵が変わったら従来どおり再構築する。
            var incremental = htmlProvider as IEditorSupportIncrementalHtmlProvider;
            pageKey = incremental?.PageContextKey(filePath, text);
            var reuseLoadedPage = incremental is not null && pageKey == _editorSupportReadyPageKey;

            // Markdown→HTML 変換は正規表現主体で重く、大きいファイルでは打鍵を固める。バックグラウンド
            // スレッドで変換し、結果だけを UI スレッドへ戻して反映する（ユーザー操作を妨げない）。
            if (reuseLoadedPage)
                body = await Task.Run(() => incremental!.RenderBody(filePath, text));
            else
                html = await Task.Run(() => htmlProvider.RenderHtml(filePath, text));

            // 変換中に新しい要求が来ていれば、そちらが最新を描くのでこのコールは降りる。
            if (seq != _editorSupportRenderSeq)
                return;
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
        _editorSupportPendingBody = body;
        _editorSupportPendingUri = uri;
        _editorSupportPendingMapFolder = mapFolder;
        _editorSupportPendingPageKey = pageKey;
        EditorSupportTitle.Text = title;

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
    /// EditorSupport が袖または俯瞰のミニチュアとして表示される状態か。
    /// Main に自動表示はしないが、VisualBrush の描画元には中身が必要なので描画だけ許可する。
    /// </summary>
    private bool IsEditorSupportInThumbnail()
    {
        if (!IsSessionEnabled(PaneKind.EditorSupport))
            return false;

        if (_stageActive)
            return _overviewActive || _stagePane != PaneKind.EditorSupport;

        return !IsShownInMain(PaneKind.EditorSupport);
    }
}
