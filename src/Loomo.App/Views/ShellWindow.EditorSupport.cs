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
using Editor.Controls.Lsp;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
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
        // 明示的なプレビュー要求＝「このファイルのプレビューを見に来た」地点。ペインフォーカスの
        // デバウンス経路とは同一ファイルでデデュープされるので二重ドットにはならない。
        RecordTrailPreview(sourceTab);
    }

    /// <summary>EditorSupport の追従先エディタタブを切り替えて内容を更新する（同一タブなら何もしない）。</summary>
    private async Task SwitchEditorSupportSourceAsync(EditorTab sourceTab, bool force = false)
    {
        if (ReferenceEquals(_editorSupportSourceTab, sourceTab))
            return;
        if (_editorSupportSourcePinned && !force && _editorSupportSourceTab is not null)
            return;

        if (_editorSupportSourceTab is not null)
        {
            _editorSupportSourceTab.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            _editorSupportSourceTab.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        // 追従先が変わる＝前ファイルの接続待ちポーリングは無効（新ファイルで作り直す）。
        StopCodeReadyRetry();

        _editorSupportSourceTab = sourceTab;
        sourceTab.Control.ViewportScrolled += EditorSupportSource_ViewportScrolled;
        // コード解析（②）のキャレット追従。非コードや案内表示中は Schedule 側で無視される。
        sourceTab.Control.CaretMoved += EditorSupportSource_CaretMoved;
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

    /// <summary>
    /// EditorSupport ヘッダーの「ブラウザで開く」：現在のプレビューを Loomo 内蔵ブラウザの新規タブへ
    /// スナップショットとして開く（以降の編集には追従しない一回きりの表示）。URI 提供者（PDF 等）は
    /// そのファイルを直接開き、HTML 提供者（Markdown/JSON プレビュー等）は現在の本文から HTML を
    /// 再生成し、画像・アセット用の仮想ホストをそのタブの CoreWebView2 にも張ってから開く
    /// （EditorSupport ペイン自身のマップはそのペインの CoreWebView2 専用で、他のタブへは及ばないため）。
    /// </summary>
    private async void OnOpenEditorSupportInBrowser(object sender, RoutedEventArgs e)
    {
        var source = _editorSupportSourceTab;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;

        var provider = _editorSupports.Resolve(filePath);
        if (provider is IEditorSupportUriProvider uriProvider)
        {
            await OpenUrlInBrowserAsync(uriProvider.ResolveNavigationUri(filePath), uriProvider.DescribeTitle(filePath));
            return;
        }

        if (provider is not IEditorSupportHtmlProvider htmlProvider)
            return; // ビジュアル提供者（CSV/TSV グリッド等）や対応の無いファイルは開ける HTML が無い。

        var title = htmlProvider.DescribeTitle(filePath);
        var mapFolder = MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder;
        var htmlText = htmlProvider.UsesEditorText ? source.Control.Text : string.Empty;
        var html = await Task.Run(() => htmlProvider.RenderHtml(filePath, htmlText));

        await OpenEditorSupportSnapshotInBrowserAsync(html, mapFolder, title);
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

        // 専用プロバイダの無いコードファイルは、LSP ベースの構造アウトラインへフォールバックする
        // （registry 外・Hex と同じ形）。専用 provider 解決の後・ビジュアル/Hex 判定より前で拾う。
        if (provider is null && filePath is not null && _codeSupport.CanHandle(filePath))
        {
            await UpdateCodeEditorSupportAsync(source, filePath);
            return;
        }

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
            // ファイル直読み系（Image/Hex/Office 等）はエディタ本文を使わない。巨大バイナリを文字列化して
            // 渡す無駄を避け、UsesEditorText の提供者にだけ Control.Text を渡す。
            await visual.UpdateAsync(filePath, visual.UsesEditorText ? source.Control.Text : string.Empty);
            return;
        }

        // WebView2 系。直前までビジュアル系を表示していたら退ける。
        HideEditorSupportVisual();

        // 本文スナップショットは UI スレッドで取る（エディタは UI スレッド専有）。重い
        // Markdown→HTML 変換はこの後バックグラウンドで行うので、ここで一度だけ読む。
        // ファイル直読み系（Office 等 UsesEditorText=false）は本文を使わないので取得しない。
        var text = (provider?.UsesEditorText ?? true) ? source.Control.Text : string.Empty;

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
    /// 専用プロバイダの無いコードファイルに対し、LSP のドキュメントシンボルから構造アウトラインと
    /// ②呼び出し解析を EditorSupport ペインへ描く（フォールバック）。表示はネイティブ WPF
    /// （<see cref="CodeOutlineView"/>）で、WebView2 は経由しない（初回コールドスタート・白フラッシュ回避）。
    /// 言語サーバー未接続／未対応なら同ビューの案内状態を出す。await を跨ぐ古い要求は
    /// <see cref="_editorSupportRenderSeq"/> で畳む。
    /// </summary>
    private async Task UpdateCodeEditorSupportAsync(EditorTab source, string filePath)
    {
        // 描画要求のシーケンス番号。LSP 呼び出しの await を跨いで最後の要求だけが描くよう畳む。
        var seq = ++_editorSupportRenderSeq;
        var lsp = GetLspManager(source);

        // 接続済み・ドキュメント準備済みに加え、LSP の現在ドキュメントが対象ファイルと一致することも
        // 条件に含める（別ファイルのアウトラインを誤って描かないため）。
        var ready = lsp is not null && lsp.IsConnected && lsp.IsDocumentReady && LspMatchesFile(lsp, filePath);

        // コードビューをペインへ載せる（直前まで CSV/Hex/WebView を出していても即差し替え＝コールドスタート無し）。
        var view = EnsureCodeOutlineView();
        ShowEditorSupportVisual(view);
        EditorSupportTitle.Text = _codeSupport.DescribeTitle(filePath);

        if (!ready)
        {
            // 追従キャッシュは破棄（キャレット追従を止める）。案内を出したまま ready へ遷移しても再描画
            // 契機が無いので、ready になるまで軽くポーリングして本描画へ差し替える（案内は描き直さない）。
            _codeOutlineRoots = null;
            _codeOutlineSource = null;
            _codeCurrentSymbolRange = null;
            _codeCurrentCaret = null;
            view.ShowNotice(LspNoticeModel.Build(_lspManagement.EvaluateForFile(filePath)));
            ScheduleCodeReadyRetry();
            return;
        }

        // ready に到達＝アウトラインを描く。接続待ちポーリングは役目を終えたので止める。
        StopCodeReadyRetry();

        IReadOnlyList<DocumentSymbol> symbols;
        try
        {
            symbols = await lsp!.RequestDocumentSymbolsAsync();
        }
        catch
        {
            // シンボル取得に失敗しても落とさない（空アウトライン＋空パネルで描く）。
            symbols = Array.Empty<DocumentSymbol>();
        }

        // 取得の await を跨いで新しい要求が来ていれば、そちらが描くのでこのコールは降りる。
        if (seq != _editorSupportRenderSeq)
            return;

        // Caret.Line/Column は 0 始まり。LSP の DocumentSymbol も 0 始まりなので current 判定はそのまま、
        // 表示・ジャンプ用の行だけ +1 して 1 始まりにする（ビュー側 DataLine1）。
        var caret = source.Control.Caret;
        // シグネチャ（Detail）は本文の宣言行から切り出すので、ここで一度だけ行分割して渡す
        // （エディタは UI スレッド専有。以降の LSP await を跨がないよう先に取る）。
        var sourceLines = SplitLines(source.Control.Text);
        var roots = CodeEditorSupport.ToOutline(symbols, sourceLines);

        // ②（呼び出し元/先・使用箇所）は<b>キャレット直下のシンボル</b>で問い合わせる（IDE の「参照を検索」相当）。
        // 返る名前範囲はキャレット追従の差分基準（このシンボル上を動く間は再取得しない）に使う。
        var (panels, symbolRange) = await FetchCallPanelsAsync(lsp!, caret.Line, caret.Column);
        if (seq != _editorSupportRenderSeq)
            return;

        // アウトラインの current ハイライトはキャレットを含む最深メンバーへ付ける。
        var member = CodeOutline.FindEnclosing(roots, caret.Line, caret.Column);
        var currentLine1 = member is null ? 0 : member.Line0 + 1;

        _codeOutlineRoots = roots;
        _codeOutlineSource = source;
        _codeCurrentSymbolRange = symbolRange;
        _codeCurrentCaret = (caret.Line, caret.Column);

        view.ShowOutline(roots, currentLine1, panels);
    }

    /// <summary>
    /// コード構造アウトラインの WPF ビューを（無ければ作って）返す。初回にジャンプ／インストール等の
    /// 操作イベントを既存導線へ配線する（以降は使い回すので一度だけ）。
    /// </summary>
    private CodeOutlineView EnsureCodeOutlineView()
    {
        if (_codeOutlineView is not null)
            return _codeOutlineView;

        var view = new CodeOutlineView();
        // アウトラインのメンバー名クリック → ソース行（1 始まり）へジャンプしてフォーカスを戻す。
        view.SourceLineActivated += (_, line1) => FocusEditorSupportSource(line1 > 0 ? line1 : null);
        // ②パネルの行クリック → 別ファイル（または同一ファイル）の該当行を開く（1 始まり→内部で 0 始まりへ）。
        view.FileLocationActivated += (_, e) => _ = OpenPathInEditorAsync(e.Path, e.Line1, column: 0);
        // 案内ページのボタン → 既存導線を再利用する。
        view.InstallRequested += (_, _) => InstallLspForEditorSupportSource();
        view.OpenLspSettingsRequested += (_, _) => _vm.LspPrompt.OpenSettingsCommand.Execute(null);
        view.OpenDocsRequested += (_, url) => _ = OpenUrlInBrowserAsync(url, null);

        _codeOutlineView = view;
        return view;
    }

    /// <summary>
    /// LSP の現在ドキュメント（<see cref="IEditorLspManager.CurrentUri"/>・file:// URI）が対象
    /// <paramref name="filePath"/> と同一ファイルを指しているか。file URI をローカルパス化して大小無視で比較する。
    /// </summary>
    private static bool LspMatchesFile(IEditorLspManager lsp, string filePath)
    {
        var current = CodeEditorSupport.TryUriToLocalPath(lsp.CurrentUri);
        if (string.IsNullOrEmpty(current))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(current), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // 無効なパス等は不一致扱い（案内ページへ）
        }
    }

    /// <summary>
    /// ②呼び出し解析を LSP から取得する。<c>PrepareCallHierarchyAsync</c> → 呼び出し元/先、
    /// <c>RequestReferencesAsync</c> → 使用箇所。<b>キャレット直下のシンボル</b>（<paramref name="line0"/>/<paramref name="col0"/>、
    /// 0 始まり）で問い合わせる＝IDE の「参照を検索」相当。callHierarchy 非対応サーバーやシンボル外の位置でも
    /// 例外を投げず、取れた分だけ（無ければ空で）返す。あわせて解決できたシンボルの名前範囲
    /// （callHierarchy の <c>SelectionRange</c>）を返し、呼び出し側のキャレット追従の差分基準に使う
    /// （このシンボル上を動く間は再取得しない）。解決できなければ範囲は null。
    /// <para>
    /// LSP 往復は互いに独立なものを<b>並列化</b>して初回結果の到達を縮める：使用箇所（references）は
    /// callHierarchy に依存しないので即開始し、呼び出し元/先（incoming/outgoing）は prepare 解決後に
    /// 2 本同時に投げる（各サーバーは複数リクエストを多重化できる）。
    /// </para>
    /// </summary>
    private static async Task<(CallPanels Panels, LspRange? SymbolRange)> FetchCallPanelsAsync(
        IEditorLspManager lsp, int line0, int col0)
    {
        // 使用箇所は prepareCallHierarchy に依存しない → 先に走らせて呼び出し元/先と並列にする。
        async Task<List<CallReference>> FetchReferencesAsync()
        {
            var list = new List<CallReference>();
            try
            {
                foreach (var r in await lsp.RequestReferencesAsync(line0, col0) ?? (IReadOnlyList<LspLocation>)Array.Empty<LspLocation>())
                    if (r is not null)
                        // 使用箇所はシンボル名を持たない（位置のみ）。行は Range.Start（SelectionRange は無い）。
                        list.Add(new CallReference("", r.Uri ?? "", r.Range?.Start?.Line ?? 0));
            }
            catch { /* references 非対応：使用箇所は空のまま */ }
            return list;
        }

        var referencesTask = FetchReferencesAsync();

        var incoming = new List<CallReference>();
        var outgoing = new List<CallReference>();
        LspRange? symbolRange = null;
        string? target = null;

        try
        {
            var item = await lsp.PrepareCallHierarchyAsync(line0, col0);
            if (item is not null)
            {
                // 解決したシンボルの名前範囲＝キャレット追従の差分基準（この範囲内の移動では再取得しない）。
                symbolRange = item.SelectionRange;
                target = item.Name; // パネル見出し用（②がどのシンボルの結果か明示）

                async Task<List<CallReference>> FetchIncomingAsync()
                {
                    var list = new List<CallReference>();
                    try
                    {
                        foreach (var c in await lsp.GetIncomingCallsAsync(item) ?? Array.Empty<CallHierarchyIncomingCall>())
                            if (c?.From is { } f)
                                list.Add(new CallReference(f.Name ?? "", f.Uri ?? "", f.SelectionRange?.Start?.Line ?? 0));
                    }
                    catch { /* incoming 非対応でも他は出す */ }
                    return list;
                }

                async Task<List<CallReference>> FetchOutgoingAsync()
                {
                    var list = new List<CallReference>();
                    try
                    {
                        foreach (var c in await lsp.GetOutgoingCallsAsync(item) ?? Array.Empty<CallHierarchyOutgoingCall>())
                            if (c?.To is { } t)
                                list.Add(new CallReference(t.Name ?? "", t.Uri ?? "", t.SelectionRange?.Start?.Line ?? 0));
                    }
                    catch { /* outgoing 非対応でも他は出す */ }
                    return list;
                }

                // 呼び出し元/先は互いに独立 → 同時に投げて両方揃うのを待つ。
                var incomingTask = FetchIncomingAsync();
                var outgoingTask = FetchOutgoingAsync();
                await Task.WhenAll(incomingTask, outgoingTask);
                incoming = incomingTask.Result;
                outgoing = outgoingTask.Result;
            }
        }
        catch { /* callHierarchy 非対応サーバー：呼び出し元/先は空のまま */ }

        var references = await referencesTask;
        return (new CallPanels(incoming, outgoing, references, target), symbolRange);
    }

    /// <summary>本文をシグネチャ抽出用に行分割する（改行種別を吸収。0 始まり index が LSP の line と一致）。</summary>
    private static IReadOnlyList<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// キャレット（0 始まり line/col）が LSP 範囲 <paramref name="range"/>（0 始まり・両端含む）の内側か。
    /// ②の差分基準：直近に解決したシンボルの名前範囲にキャレットが留まる間は同じシンボル＝再取得しない。
    /// </summary>
    private static bool CaretInRange(LspRange range, int line0, int col0)
    {
        var start = range.Start;
        var end = range.End;
        if (start is null || end is null)
            return false;
        if (line0 < start.Line || line0 > end.Line)
            return false;
        if (line0 == start.Line && col0 < start.Character)
            return false;
        if (line0 == end.Line && col0 > end.Character)
            return false;
        return true;
    }

    /// <summary>
    /// キャレット追従（②の再取得）。<see cref="ScheduleCodeCallPanelsRefresh"/> のデバウンス満了で呼ばれる。
    /// ②は<b>キャレット直下のシンボル</b>で問い合わせる（IDE の「参照を検索」相当）。直近に解決したシンボルの
    /// 名前範囲（<see cref="_codeCurrentSymbolRange"/>）にキャレットが留まる間は同じシンボル＝再取得しない。
    /// 範囲が取れなかった（変数・空白上等）ときはキャレット位置（<see cref="_codeCurrentCaret"/>）で差分を取る。
    /// アウトライン（＝ドキュメントシンボル）は取り直さない（構造は編集でしか変わらない）。
    /// </summary>
    private async Task RefreshCodeCallPanelsAsync()
    {
        var source = _editorSupportSourceTab;
        if (source is null)
            return;

        // コードページを描いていない／別タブへ移った＝追従対象外。
        var roots = _codeOutlineRoots;
        if (roots is null || !ReferenceEquals(_codeOutlineSource, source))
            return;

        // ペインが見えていなければ描かない（通常更新と同じゲート）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(
                onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        var caret = source.Control.Caret;

        // 同じシンボル上（前回解決した名前範囲の内側）の移動、または範囲が取れなかった前回と同一キャレット位置の
        // ままなら②を再取得しない。※キャッシュ更新は実描画の直前まで遅延する（await 後に降りたら旧値で再試行）。
        if (_codeCurrentSymbolRange is { } range && CaretInRange(range, caret.Line, caret.Column))
            return;
        if (_codeCurrentSymbolRange is null && _codeCurrentCaret is { } prev
            && prev.Line == caret.Line && prev.Col == caret.Column)
            return;

        var filePath = source.Control.FilePath;
        if (filePath is null)
            return;
        var lsp = GetLspManager(source);
        if (lsp is null || !lsp.IsConnected || !lsp.IsDocumentReady || !LspMatchesFile(lsp, filePath))
            return;

        var seq = ++_editorSupportRenderSeq;
        // ②はキャレット直下のシンボルで問い合わせる（解決できなければ空パネル＋範囲 null）。
        var (panels, symbolRange) = await FetchCallPanelsAsync(lsp, caret.Line, caret.Column);
        if (seq != _editorSupportRenderSeq)
            return;

        // アウトラインの current ハイライトはキャレットを含む最深メンバーへ付ける。
        var member = CodeOutline.FindEnclosing(roots, caret.Line, caret.Column);
        var currentLine1 = member is null ? 0 : member.Line0 + 1; // current を付け替える行（1 始まり）

        // ツリーは作り直さず current 付替え＋②パネル差し替えだけ（折りたたみ状態・スクロールを保つ）。
        _codeCurrentSymbolRange = symbolRange;
        _codeCurrentCaret = (caret.Line, caret.Column);
        _codeOutlineView?.SetCurrentAndPanels(currentLine1, panels);
    }

    /// <summary>
    /// コード案内ページの「インストール」導線。現在の追従元ファイルの拡張子から対応サーバーを再判定し、
    /// 可視ターミナルでインストールコマンドを実行する（端末未接続や導入済みなら何もしない）。
    /// </summary>
    private void InstallLspForEditorSupportSource()
    {
        var filePath = _editorSupportSourceTab?.Control.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return;

        var info = _lspManagement.EvaluateForFile(filePath);
        if (info is null)
            return; // 既に導入済み等（案内ボタンが古い）：何もしない

        // 実行結果（可視ターミナルの有無）に関わらず、案内ページ自体はそのまま（LSP 接続後の
        // 再オープン／再描画で自然にアウトラインへ切り替わる）。
        _lspManagement.InstallForPrompt(info);
    }

    /// <summary>案内表示中に ready を待つポーリングの間隔。短くして「ファイル切替→結果表示」の待ちを詰める。</summary>
    private static readonly TimeSpan CodeReadyRetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// ready を待つポーリングの最大試行回数（<see cref="CodeReadyRetryInterval"/> 間隔で約 25 秒に相当）。
    /// サーバーが永久に来ないケースの保険。
    /// </summary>
    private const int CodeReadyMaxRetries = 125;

    /// <summary>
    /// 案内（言語サーバー接続待ち）を出したあと、ready へ遷移したかを <see cref="CodeReadyRetryInterval"/> 間隔で
    /// 確認するポーリングを開始する。既に動いていれば何もしない。ready でない間は<b>案内を描き直さない</b>
    /// （チカチカ防止）＝ready になった tick でだけ本描画（<see cref="UpdateCodeEditorSupportAsync"/> の ready 分岐）へ差し替える。
    /// </summary>
    private void ScheduleCodeReadyRetry()
    {
        if (_codeReadyRetryTimer is null)
        {
            _codeReadyRetryTimer = new DispatcherTimer { Interval = CodeReadyRetryInterval };
            _codeReadyRetryTimer.Tick += CodeReadyRetry_Tick;
        }

        if (!_codeReadyRetryTimer.IsEnabled)
        {
            _codeReadyRetryAttempts = 0;
            _codeReadyRetryTimer.Start();
        }
    }

    /// <summary>接続待ちポーリングを止める（ready 到達・対象変更・上限で呼ぶ）。</summary>
    private void StopCodeReadyRetry()
    {
        _codeReadyRetryTimer?.Stop();
        _codeReadyRetryAttempts = 0;
    }

    private async void CodeReadyRetry_Tick(object? sender, EventArgs e)
    {
        var source = _editorSupportSourceTab;
        var filePath = source?.Control.FilePath;

        // 対象が変わった／コード外／既にアウトライン描画済み → 停止。
        if (source is null || filePath is null || !_codeSupport.CanHandle(filePath) || _codeOutlineRoots is not null)
        {
            StopCodeReadyRetry();
            return;
        }

        if (++_codeReadyRetryAttempts > CodeReadyMaxRetries)
        {
            StopCodeReadyRetry(); // サーバーが来ない：案内のまま諦める（ペイン再オープンで再試行される）
            return;
        }

        // 非表示中は描かない（が、次 tick で再確認する＝ポーリングは継続）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(
                onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        var lsp = GetLspManager(source);
        var ready = lsp is not null && lsp.IsConnected && lsp.IsDocumentReady && LspMatchesFile(lsp, filePath);
        if (!ready)
            return; // まだ：既存の案内のまま、次 tick で再確認（案内を描き直さない）。

        // ready 到達：本描画へ差し替える（ready 分岐が StopCodeReadyRetry する）。
        await UpdateCodeEditorSupportAsync(source, filePath);
    }

    /// <summary>キャレット追従（②再取得）を 150ms デバウンスする。コードページ未描画のときは無視。</summary>
    private void ScheduleCodeCallPanelsRefresh()
    {
        // コードページを描いていなければ追従不要（非コードファイル・案内表示中など）。
        if (_codeOutlineRoots is null)
            return;

        if (_codeCaretTimer is null)
        {
            _codeCaretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _codeCaretTimer.Tick += async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await RefreshCodeCallPanelsAsync();
            };
        }

        _codeCaretTimer.Stop();
        _codeCaretTimer.Start();
    }

    /// <summary>追従元エディタのキャレット移動：コード解析（②）をデバウンスして追従する。</summary>
    private void EditorSupportSource_CaretMoved(object? sender, CaretInfo e)
        => ScheduleCodeCallPanelsRefresh();

    /// <summary>
    /// Markdown プレビューでタスクリストのチェックボックスをクリックしたときの反映。
    /// 対応するソース行（0始まり、プレビュー生成時にフロントマター分ずらして埋め込んだもの）の
    /// <c>[ ]</c>/<c>[x]</c> を反転してエディタの本文を書き換える。Vim のモード（挿入中など）に依存
    /// せず安全に書き換えられるよう、キー入力を経由しない <see cref="VimEditorControl.SetText"/> を使う
    /// （プレビュー側のクリックはエディタの現在モードと無関係に届くため、ex コマンド経由だと挿入モード中に
    /// コロンがそのまま入力されてしまう）。
    /// </summary>
    private void ToggleMarkdownTaskCheckbox(int lineIndex)
    {
        var source = _editorSupportSourceTab;
        if (source is null)
            return;

        var text = source.Control.Text;
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length)
            return;

        var toggled = MarkdownRenderer.ToggleTaskListLine(lines[lineIndex]);
        if (toggled is null)
            return;

        lines[lineIndex] = toggled;
        source.Control.SetText(string.Join(eol, lines));
        ScheduleEditorSupportUpdate();
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
