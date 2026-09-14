using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// DiffSessionView の「Markdown 差分のレンダリング表示」パート（設計書 §24.10）。
///
/// <para>作りは部屋の他のプレビューと同じ流儀にそろえてある：WebView2 は
/// <see cref="IEditorSupportViewFactory"/> で作り（<see cref="LoomoWebView2"/> ＝0サイズで落ちない）、
/// 生成 HTML は <see cref="EditorSupportNavigationService"/> で一時ページへ書いて <c>page.loomo</c>
/// 仮想ホストから開く（<c>NavigateToString</c> の約2MB上限を避ける）。mermaid の配信元 <c>assets.loomo</c>、
/// 相対パス画像の <c>preview.loomo</c> も同じ仕込み。</para>
///
/// <para><b>WebView2 はモードを切ったときに初めて作る</b>。Diff ペインは常時開いていることがあり、
/// テキスト差分しか見ない人のために常駐のブラウザプロセスを増やさない。<b>一度作ったらセッション中は
/// 常駐する</b>（テキストへ戻しても・ペインを隠しても捨てない）——切り離しの複製プレビューと同じ判断で、
/// 見比べのために何度も往復するモードで作り直しの数秒を毎回払わせないため。捨てるのはウィンドウ移動と
/// ブラウザプロセスの落ちたときだけ。</para>
/// </summary>
public partial class DiffSessionView
{
    private IEditorSupportViewFactory? _markdownViewFactory;
    private EditorSupportNavigationService? _markdownNavigation;
    private WebView2CompositionControl? _markdownWeb;
    private Task<bool>? _markdownInit;
    /// <summary>WebView2 を載せたときのウィンドウ。別ウィンドウへ移されたら作り直す
    /// （コンポジションビジュアルは元ウィンドウのコンポジタに紐づいたままで、移すと空表示になる）。</summary>
    private Window? _markdownWindow;
    /// <summary>描画の世代番号。await の間に新しい HTML が来たら古い方の適用を捨てる。</summary>
    private int _markdownRenderSeq;
    /// <summary>今のページの読み込みが終わっているか。終わる前のスクロールは読み込み完了で上書きされるので待たせる。</summary>
    private bool _markdownPageReady;
    /// <summary>ページの読み込み待ちの「次/前の差分」の行き先（変更グループ番号・-1 は無し）。
    /// ファイルを開いた直後の自動ジャンプは、ほぼ必ずページの読み込みより先に来る。</summary>
    private int _markdownPendingChange = -1;
    /// <summary>いま読み込ませている差分ページの URL（一時ページは描画ごとに ?v= で別 URL）。
    /// 読み込み完了はこれと一致したときだけ数える——前のページの完了で「読み込み済み」にすると、
    /// 新しいファイルの行き先を古いページへ当てて使い切ってしまう。</summary>
    private string? _markdownNavigatingUrl;

    /// <summary>描き直しを諦めた（書き出し・初期化・遷移の失敗）。前のページはそのまま出ているので、
    /// 待たせていた行き先はそこへ当てる——待たせたままだと「次/前の差分」が以後ずっと無反応になる。</summary>
    private void AbandonMarkdownNavigation()
    {
        _markdownNavigatingUrl = null;
        _markdownPageReady = true;
        FlushMarkdownPendingChange();
    }

    /// <summary>レンダリング差分の <paramref name="index"/> 番目の変更グループへスクロールする
    /// （ページが読み込み中なら、読み込み完了まで持ち越す）。</summary>
    private void ScrollMarkdownToChange(int index)
    {
        _markdownPendingChange = index;
        FlushMarkdownPendingChange();
    }

    /// <summary>WebView2 の <c>Source</c> は正規化されて返ることがあるので、文字列ではなく URI として比べる。</summary>
    private static bool IsSameUrl(string? actual, string expected)
        => Uri.TryCreate(actual, UriKind.Absolute, out var a)
           && Uri.TryCreate(expected, UriKind.Absolute, out var b)
           && a == b;

    private void FlushMarkdownPendingChange()
    {
        if (_markdownPendingChange < 0 || !_markdownPageReady || _markdownWeb?.TryCore() is not { } core)
            return;
        var index = _markdownPendingChange;
        _markdownPendingChange = -1;
        try { _ = core.ExecuteScriptAsync(MarkdownDiffPage.ScrollToChangeScript(index)); }
        catch { /* 描き直しで core が捨てられた直後など。次の描画でやり直せる */ }
    }

    /// <summary>
    /// レンダリング差分の本文でリンクが押された（生の href と、その差分の出どころのファイル）。
    /// 宛先の振り分け（URL＝ブラウザ／ファイル＝エディタ・相対パスの解決）は部屋の既存の経路
    /// （<c>ShellWindow.HandleEditorSupportLinkClickedAsync</c>）が持っているので、そこへ中継する。
    /// ページ側 JS はリンクの既定遷移を止めて <c>linkClicked</c> を投げてくるので、これを繋がないと
    /// <b>本文のリンクが完全に無反応</b>になる。
    /// </summary>
    public event EventHandler<(string Href, string? SourcePath)>? MarkdownLinkClicked;

    /// <summary>
    /// ホスト（ShellWindow）から WebView2 の作り方と一時ページの置き場を受け取る。この UserControl は
    /// XAML から生えるので DI が届かない——渡されなければレンダリング表示は動かない（テキスト差分は通常どおり）。
    /// </summary>
    /// <param name="instanceKey">一時ページのファイル名を分ける鍵。Diff のビューは<b>同時に複数</b>
    /// 生きうる（ペイン＋切り離しウィンドウ）ので、既定名のままだと互いの本文を上書きし合って、
    /// 窓を開けた瞬間からペイン側のレンダリング表示まで壊れる。</param>
    public void ConfigureMarkdownRender(
        IEditorSupportViewFactory factory, string previewFolder, string? instanceKey = null)
    {
        _markdownViewFactory = factory;
        // EditorSupport のプレビューと同じフォルダーを共有しつつ、ファイル名は分ける
        // （同じ名前だと互いの本文を上書きし合う）。掃除の glob（preview-*.html）には引っかかる名前にする。
        var suffix = string.IsNullOrEmpty(instanceKey) ? "" : $"-{instanceKey}";
        _markdownNavigation = new EditorSupportNavigationService(
            previewFolder, $"preview-diff-{Environment.ProcessId}{suffix}.html");
        // 落ちたインスタンスや前回の版が置いていった一時ページの掃除（起動を待たせないよう裏で）。
        var navigation = _markdownNavigation;
        Task.Run(() => navigation.CleanStalePages(TimeSpan.FromDays(1)));
    }

    /// <summary>VM の表示モード／HTML の変化を受けて描き直す。</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiffSessionViewModel.MarkdownRenderHtml)
            or nameof(DiffSessionViewModel.IsMarkdownRenderActive))
            _ = ApplyMarkdownRenderAsync();
    }

    /// <summary>再ペアレント（別ウィンドウへの移動）を検出したら WebView2 を作り直して描き直す。</summary>
    private void ReattachMarkdownWebIfMoved()
    {
        if (_markdownWeb is null || ReferenceEquals(_markdownWindow, Window.GetWindow(this)))
            return;
        DisposeMarkdownWeb();
        _ = ApplyMarkdownRenderAsync();
    }

    private async Task ApplyMarkdownRenderAsync()
    {
        var seq = ++_markdownRenderSeq;
        // モードが効いていない間は何も作らない（WebView2 の実体化はここが唯一の入口）。
        if (Vm is not { IsMarkdownRenderActive: true } vm)
            return;
        if (vm.MarkdownRenderHtml is not { Length: > 0 } html)
        {
            // 出せなかった（大きすぎる・差分が無い）。理由の裏に前の差分を出したままにしない。
            if (_markdownWeb is not null)
                _markdownWeb.Visibility = Visibility.Collapsed;
            return;
        }

        // これから別のページへ差し替える。前のページへ新しい差分の行き先を当てないよう、読み込み完了まで待たせる。
        _markdownPageReady = false;
        _markdownNavigatingUrl = null;
        var web = await EnsureMarkdownWebAsync();
        if (seq != _markdownRenderSeq)
            return;   // 新しい描画が持ち主
        if (web is null || web.TryCore() is not { } core)
        {
            AbandonMarkdownNavigation();
            return;
        }

        // 相対パス画像の解決先をこの差分のファイルへ合わせる（マルチルートの基準は VM 側で解決済み）。
        _markdownNavigation!.UpdatePreviewHost(core, vm.MarkdownRenderMapFolder);

        var navigation = _markdownNavigation;
        var url = await Task.Run(() => navigation.TryWritePage(html, out var written) ? written : null);
        if (seq != _markdownRenderSeq)
            return;
        if (url is null || web.TryCore() is not { } current)
        {
            AbandonMarkdownNavigation();
            return;
        }
        web.Visibility = Visibility.Visible;
        _markdownNavigatingUrl = url;
        try { current.Navigate(url); }
        catch { AbandonMarkdownNavigation(); /* 描けなければ前の表示のまま */ }
    }

    private async Task<WebView2CompositionControl?> EnsureMarkdownWebAsync()
    {
        if (_markdownViewFactory is null || _markdownNavigation is null)
            return null;
        if (_markdownWeb is null)
        {
            // 生成プロパティ無し（＝既定プロファイル）で作る。切り離しの複製プレビューと同じ判断で、
            // 共有プロファイルの引数競合（§21.5.3）に巻き込まれずに必ず作れる方を採る。
            _markdownWeb = _markdownViewFactory.Create();
            MarkdownRenderHost.Children.Insert(0, _markdownWeb);
            _markdownWindow = Window.GetWindow(this);
            _markdownInit = null;
        }
        // await をまたぐので、この呼び出しが相手にしている WebView2 と初期化はローカルで掴んでおく
        // （待っている間に作り直されたら、古い方を返さず次の要求へ譲る）。
        var web = _markdownWeb;
        var init = _markdownInit ??= InitMarkdownCoreAsync(web);
        if (!await init)
        {
            if (ReferenceEquals(_markdownInit, init))
                _markdownInit = null;   // やり直せる状態に戻す（別の生成が始まっていたら触らない）
            return null;
        }
        return ReferenceEquals(_markdownWeb, web) ? web : null;
    }

    private async Task<bool> InitMarkdownCoreAsync(WebView2CompositionControl web)
    {
        if (_markdownViewFactory is null || !await _markdownViewFactory.InitializeAsync(web))
            return false;
        if (web.TryCore() is not { } core)
            return false;

        // assets.loomo（mermaid）と page.loomo（一時ページ）。preview.loomo は差分ごとに張り替える。
        _markdownNavigation!.ConfigureVirtualHosts(core, null);
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        // 差分は「読む場所」なので、ページから出ていく遷移は受けない（本文中のリンクを踏んで
        // 差分が消え、戻る口も無い、という行き止まりを作らないため）。
        core.NavigationStarting += (_, e) =>
        {
            // about:blank は WebView2 自身の初期化ぶん（止めると白紙にすらならない）。
            if (!EditorSupportNavigationService.IsPreviewUrl(e.Uri)
                && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        // 差分ページの読み込みが終わったら、待たせていた「次/前の差分」（開いた直後の自動ジャンプ）を当てる。
        // 数えるのは<b>いま読み込ませているページ</b>の完了だけ（about:blank や前のページの完了は数えない）。
        // 失敗でも「待ち」は解く——解かないと以後の「次/前の差分」が全部待たされたまま無反応になる。
        core.NavigationCompleted += (_, _) =>
        {
            if (!ReferenceEquals(_markdownWeb, web) || _markdownNavigatingUrl is not { } expected
                || !IsSameUrl(core.Source, expected))
                return;
            _markdownNavigatingUrl = null;
            _markdownPageReady = true;
            FlushMarkdownPendingChange();
        };
        core.WebMessageReceived += OnMarkdownWebMessageReceived;
        // ブラウザプロセスが落ちたら作り直して描き直す（放っておくと空のまま残る）。
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DisposeMarkdownWeb();
                _ = ApplyMarkdownRenderAsync();
            }));
        };
        return true;
    }

    /// <summary>ページ側 JS からのメッセージ。扱うのは <c>linkClicked</c> だけ——スクロール同期も
    /// タスクリストのチェックも、この差分には書き戻す先が無い（チェックボックスは CSS で押せなくしてある）。</summary>
    private void OnMarkdownWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "linkClicked"
                && root.TryGetProperty("href", out var hrefElement)
                && hrefElement.GetString() is { Length: > 0 } href)
                MarkdownLinkClicked?.Invoke(this, (href, Vm?.SelectedFile?.FullPath));
        }
        catch { /* 解釈できないメッセージは捨てる */ }
    }

    private void DisposeMarkdownWeb()
    {
        if (_markdownWeb is null)
            return;
        MarkdownRenderHost.Children.Remove(_markdownWeb);
        _markdownViewFactory?.Dispose(_markdownWeb);
        // 仮想ホストのマップは core と一緒に消えるので、「マップ済み」の記憶も捨てる。
        _markdownNavigation?.ResetPreviewHost();
        _markdownWeb = null;
        _markdownInit = null;
        _markdownWindow = null;
        _markdownPageReady = false;
        _markdownNavigatingUrl = null;
    }
}
