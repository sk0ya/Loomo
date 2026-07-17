using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Editor.Controls;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: コマンドパレット（部屋全体の操作統一）。移動・ペイン表示・タブ・コンポーザ・
/// ペグボード・サイドバー・ワークスペース切替といった既存操作に名前を付け、
/// Ctrl+Shift+P（または Ctrl+W p）から検索して実行できるようにする。
/// 一覧は開くたびに現在状態（ステージ中か・WS一覧など）から組み直す。
/// 絞り込みロジックは <see cref="PaletteFilter"/>（純ロジック・テスト済み）。
/// </summary>
public partial class ShellWindow
{
    private IReadOnlyList<PaletteCommand> _paletteCommands = Array.Empty<PaletteCommand>();

    /// <summary>@／# モードの非同期検索を、入力が変わるたびにキャンセル＆再発行するためのトークン源。</summary>
    private CancellationTokenSource? _paletteSearchCts;

    /// <summary>パレットの入力モード。先頭の記号で切り替える（VS Code 風）。既定は「すべて」（横断検索）。
    /// クラス／シンボルは LSP（ワークスペースシンボル）経由で取得する。</summary>
    private enum PaletteMode { All, File, Grep, Class, Symbol, Terminal, Command }

    private bool IsPaletteOpen => CommandPaletteOverlay.Visibility == Visibility.Visible;

    private void OpenCommandPalette()
    {
        _paletteCommands = BuildPaletteCommands();
        // テーマ／フォントが前回開いてから変わっていることがあるので、プレビュー用エディタへ再適用する。
        if (_previewEditor is not null)
            ApplyEditorAppearance(_previewEditor);
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        UpdatePaletteBoxSize();
        PaletteInput.Text = string.Empty;
        RefilterPalette();
        PaletteInput.Focus();
    }

    /// <summary>プレビューを開いているか（＝箱の背を高く固定するか）。RefilterPalette が更新する。</summary>
    private bool _palettePreviewShown;

    /// <summary>パレット本体の大きさをウィンドウに合わせて広げる（画面が広いほど一覧＋プレビューを広く取る）。
    /// 幅・高さともにウィンドウの割合をとりつつ下限・上限でクランプする。オーバーレイの SizeChanged からも呼ぶ
    /// ので、開いたまま／最大化しても追従する。プレビュー表示中は候補が少なくてもプレビューを大きく見せたいので
    /// 背を高く固定し、一覧だけのときは中身なりに縮める（無駄な余白を出さない）。</summary>
    private void UpdatePaletteBoxSize()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        PaletteBox.Width = Math.Clamp(w * 0.72, 760, 1600);
        var tall = Math.Max(440, h * 0.82);
        PaletteBox.MaxHeight = tall;
        PaletteBox.Height = _palettePreviewShown ? tall : double.NaN;
    }

    private void OnPaletteOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsPaletteOpen)
            UpdatePaletteBoxSize();
    }

    /// <param name="refocus">true なら直前にフォーカスしていたペインへ戻す（Esc・背景クリック時）。
    /// コマンド実行時は実行先がフォーカスを決めるので false。</param>
    private void CloseCommandPalette(bool refocus)
    {
        if (!IsPaletteOpen)
            return;
        _paletteSearchCts?.Cancel();
        _palettePreviewCts?.Cancel();
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }

    /// <summary>先頭記号でモードと素のクエリへ分解する。@＝ファイル名、#＝grep、:＝クラス、%＝シンボル、
    /// $＝ターミナル内検索、&gt;＝コマンド、無印＝すべて（横断検索）。</summary>
    private static (PaletteMode Mode, string Query) ParsePaletteMode(string? text)
    {
        text ??= string.Empty;
        if (text.StartsWith('@')) return (PaletteMode.File, text[1..].Trim());
        if (text.StartsWith('#')) return (PaletteMode.Grep, text[1..].Trim());
        if (text.StartsWith(':')) return (PaletteMode.Class, text[1..].Trim());
        if (text.StartsWith('%')) return (PaletteMode.Symbol, text[1..].Trim());
        if (text.StartsWith('$')) return (PaletteMode.Terminal, text[1..].Trim());
        if (text.StartsWith('>')) return (PaletteMode.Command, text[1..].Trim());
        return (PaletteMode.All, text);
    }

    /// <summary>そのモードの先頭記号（すべては無印）。</summary>
    private static string ModePrefix(PaletteMode mode) => mode switch
    {
        PaletteMode.File => "@",
        PaletteMode.Grep => "#",
        PaletteMode.Class => ":",
        PaletteMode.Symbol => "%",
        PaletteMode.Terminal => "$",
        PaletteMode.Command => ">",
        _ => string.Empty,  // All（無印）
    };

    /// <summary>素のクエリは保ったままモードだけ差し替える（先頭記号を付け替えてキャレットを末尾へ）。
    /// マウスでのチップ選択・Ctrl+Shift+P 連打の両方から呼ばれる。</summary>
    private void SetPaletteMode(PaletteMode mode)
    {
        var (_, query) = ParsePaletteMode(PaletteInput.Text);
        PaletteInput.Text = ModePrefix(mode) + query;     // TextChanged が RefilterPalette を呼ぶ
        PaletteInput.CaretIndex = PaletteInput.Text.Length;
        PaletteInput.Focus();
    }

    /// <summary>すべて → ファイル → テキスト → クラス → シンボル → ターミナル → コマンド → すべて…
    /// とチップ表示順で巡回する（Ctrl+Shift+P 連打）。</summary>
    private void CyclePaletteMode()
    {
        var (mode, _) = ParsePaletteMode(PaletteInput.Text);
        var next = mode switch
        {
            PaletteMode.All => PaletteMode.File,
            PaletteMode.File => PaletteMode.Grep,
            PaletteMode.Grep => PaletteMode.Class,
            PaletteMode.Class => PaletteMode.Symbol,
            PaletteMode.Symbol => PaletteMode.Terminal,
            PaletteMode.Terminal => PaletteMode.Command,
            _ => PaletteMode.All,
        };
        SetPaletteMode(next);
    }

    private void OnPaletteModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaletteMode>(tag, out var mode))
            SetPaletteMode(mode);
    }

    /// <summary>現在モードのチップを強調する（選択中＝Accent 枠＋通常文字色、他は淡色）。</summary>
    private void UpdateModeChips(PaletteMode mode)
    {
        Highlight(PaletteModeAll, mode == PaletteMode.All);
        Highlight(PaletteModeFile, mode == PaletteMode.File);
        Highlight(PaletteModeGrep, mode == PaletteMode.Grep);
        Highlight(PaletteModeClass, mode == PaletteMode.Class);
        Highlight(PaletteModeSymbol, mode == PaletteMode.Symbol);
        Highlight(PaletteModeTerminal, mode == PaletteMode.Terminal);
        Highlight(PaletteModeCommand, mode == PaletteMode.Command);

        static void Highlight(Button chip, bool active)
        {
            if (active)
            {
                chip.SetResourceReference(Control.BorderBrushProperty, "Accent");
                chip.SetResourceReference(Control.ForegroundProperty, "Fg");
            }
            else
            {
                chip.BorderBrush = System.Windows.Media.Brushes.Transparent;
                chip.SetResourceReference(Control.ForegroundProperty, "FgDim");
            }
        }
    }

    private void RefilterPalette()
    {
        var (mode, query) = ParsePaletteMode(PaletteInput.Text);
        UpdateModeChips(mode);

        // 箱の幅は固定（モード切替で左右にズレないように）。ファイル/テキスト/クラス/シンボル検索は
        // 右にプレビュー枠を開く（該当のファイル・行をスニペット表示）。すべて（横断）は入力後だけ開く
        // （開いた直後のコマンド一覧では空プレビューを出さない）。ターミナル検索は実ターミナル側で
        // ハイライト＋ジャンプするのでプレビューは持たない。
        var showPreview = mode is PaletteMode.File or PaletteMode.Grep or PaletteMode.Class or PaletteMode.Symbol
            || (mode == PaletteMode.All && !string.IsNullOrWhiteSpace(query));
        // プレビューは一覧と同じ割合（★）で開くので、箱がウィンドウに合わせて広がると一緒に大きくなる。
        PalettePreviewColumn.Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // 候補が少なくてもプレビューが縮まないよう、開いている間は箱の背を高く固定する（UpdatePaletteBoxSize）。
        _palettePreviewShown = showPreview;
        UpdatePaletteBoxSize();

        if (mode == PaletteMode.Command)
        {
            _paletteSearchCts?.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, query));
            return;
        }

        if (mode == PaletteMode.Terminal)
        {
            _paletteSearchCts?.Cancel();
            ShowPaletteItems(BuildTerminalMatches(query));
            return;
        }

        // すべて（空クエリ）は、開いた直後の既定表示としてコマンド一覧を即時に出す
        // （ファイル検索・LSP を走らせず、Ctrl+Shift+P からの表示を軽く保つ）。
        if (mode == PaletteMode.All && string.IsNullOrWhiteSpace(query))
        {
            _paletteSearchCts?.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, string.Empty));
            return;
        }

        _ = RefilterSearchAsync(mode, query);
    }

    /// <summary>非同期モード（すべて／ファイル／テキスト／クラス／シンボル）の検索。直前の検索を
    /// キャンセルし、軽くデバウンスしてから走らせる。</summary>
    private async Task RefilterSearchAsync(PaletteMode mode, string query)
    {
        _paletteSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _paletteSearchCts = cts;
        var ct = cts.Token;

        try
        {
            await Task.Delay(120, ct); // 連続入力をまとめる

            IReadOnlyList<PaletteCommand> items = mode switch
            {
                PaletteMode.File => (await _search.FindFilesAsync(query, 50, ct)).Select(FileEntry).ToList(),
                PaletteMode.Grep => await BuildGrepMatchesAsync(query, ct),
                PaletteMode.Class => await BuildSymbolMatchesAsync(query, isClass: true, ct),
                PaletteMode.Symbol => await BuildSymbolMatchesAsync(query, isClass: false, ct),
                _ => await BuildAllMatchesAsync(query, ct),   // All（横断）
            };

            if (!ct.IsCancellationRequested)
                ShowPaletteItems(items);
        }
        catch (OperationCanceledException) { /* 新しい入力に置き換わった */ }
    }

    /// <summary>テキスト検索（#）。空クエリは検索しない。</summary>
    private async Task<IReadOnlyList<PaletteCommand>> BuildGrepMatchesAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(query))
            return Array.Empty<PaletteCommand>();
        var hits = await _search.GrepAsync(query, new GrepOptions(MaxResults: 200), ct);
        return hits.Select(h => GrepEntry(h, query)).ToList();
    }

    /// <summary>
    /// クラス（:）／シンボル（%）検索。LSP のワークスペースシンボルから引く（<paramref name="isClass"/> で
    /// クラス限定／全シンボルを切り替える）。空クエリは案内行だけ、言語サーバー未接続なら案内行を出す。
    /// </summary>
    private async Task<IReadOnlyList<PaletteCommand>> BuildSymbolMatchesAsync(
        string query, bool isClass, CancellationToken ct)
    {
        var label = isClass ? "クラス" : "シンボル";
        if (string.IsNullOrWhiteSpace(query))
            return new[] { SymbolStatus($"入力して{label}を検索") };

        var managers = ConnectedCodeLspManagers();
        if (managers.Count == 0)
            return new[] { SymbolStatus("言語サーバーが未接続です（対象コードのファイルを開いてください）") };

        var syms = await MergeWorkspaceSymbolsAsync(managers, query, isClass, ct);
        if (ct.IsCancellationRequested)
            return Array.Empty<PaletteCommand>();
        if (syms.Count == 0)
            return new[] { SymbolStatus("一致なし") };

        return syms.Take(200).Select(s => SymbolEntry(s, label)).ToList();
    }

    /// <summary>
    /// すべて（無印）：コマンド＋ファイル＋クラス＋シンボルを横断し、カテゴリ順に混在させて返す。
    /// LSP（クラス／シンボル）は言語サーバー接続時のみ。各カテゴリは件数を抑えて一覧が偏らないようにする。
    /// </summary>
    private async Task<IReadOnlyList<PaletteCommand>> BuildAllMatchesAsync(string query, CancellationToken ct)
    {
        var items = new List<PaletteCommand>();

        // ファイル。
        var files = await _search.FindFilesAsync(query, 12, ct);
        if (ct.IsCancellationRequested)
            return items;
        items.AddRange(files.Select(FileEntry));

        // クラス／シンボル（コードファイルの言語サーバー接続時のみ。未接続なら黙って省く）。
        var managers = ConnectedCodeLspManagers();
        if (managers.Count > 0)
        {
            var classes = await MergeWorkspaceSymbolsAsync(managers, query, isClass: true, ct);
            if (ct.IsCancellationRequested)
                return items;
            items.AddRange(classes.Take(10).Select(s => SymbolEntry(s, "クラス")));

            var symbols = await MergeWorkspaceSymbolsAsync(managers, query, isClass: false, ct);
            if (ct.IsCancellationRequested)
                return items;
            // クラス系は上のグループで既出なので、シンボルグループからは除いて重複を避ける。
            items.AddRange(symbols.Where(s => !IsClassKind(s.Kind)).Take(10).Select(s => SymbolEntry(s, "シンボル")));
        }

        // コマンド（同期・軽い）は末尾に回す（すべてモードではファイル／シンボルを優先して見せる）。
        items.AddRange(PaletteFilter.Filter(_paletteCommands, query).Take(8));

        return items;
    }

    /// <summary>
    /// ワークスペースシンボルを引ける言語サーバー（＝<b>コードファイルのタブ</b>に紐づく接続済みマネージャ）を
    /// すべて集める。アクティブタブが Markdown 等だと、そのサーバー（marksman 等）がプロジェクトのクラス／
    /// シンボルではなく見出しを返してしまう（アクティブタブに引っ張られる問題）ため、アクティブタブ一つに
    /// 頼らず全タブから集め、<see cref="CodeEditorSupport.CanHandle"/> で<b>コード拡張子に絞る</b>
    /// （Markdown/JSON/CSV 等のサーバーは対象外）。アクティブなコードタブは先頭に置いて結果を優先させる。
    /// マネージャ実体で重複排除する。
    /// </summary>
    private IReadOnlyList<IEditorLspManager> ConnectedCodeLspManagers()
    {
        var seen = new HashSet<IEditorLspManager>();
        var result = new List<IEditorLspManager>();

        void TryAdd(EditorTab? tab)
        {
            // コードファイルのタブだけ（未接続・非コード・未実体化は除外）。非対応サーバーでも
            // GetWorkspaceSymbolsAsync は空を返す（MergeWorkspaceSymbolsAsync で吸収）ので接続だけ条件にする。
            if (tab is null || !_codeSupport.CanHandle(tab.PeekFilePath))
                return;
            var lsp = GetLspManager(tab);
            if (lsp is { IsConnected: true } && seen.Add(lsp))
                result.Add(lsp);
        }

        TryAdd(_activeEditorTab);              // アクティブなコードタブを優先（結果が先頭に来る）
        foreach (var tab in _editorTabs)
            TryAdd(tab);

        return result;
    }

    /// <summary>複数の言語サーバーからワークスペースシンボルを引いて統合する（プロジェクト＝複数言語横断）。
    /// 各サーバーの結果を名前＋位置で重複排除する。個々の取得は失敗しても落とさず空で飛ばす。</summary>
    private static async Task<IReadOnlyList<LspSymbolInformation>> MergeWorkspaceSymbolsAsync(
        IReadOnlyList<IEditorLspManager> managers, string query, bool isClass, CancellationToken ct)
    {
        var merged = new List<LspSymbolInformation>();
        var seen = new HashSet<(string Name, string Uri, int Line)>();

        foreach (var lsp in managers)
        {
            IReadOnlyList<LspSymbolInformation> syms;
            try { syms = await lsp.GetWorkspaceSymbolsAsync(query, isClass, ct); }
            catch { continue; }
            if (ct.IsCancellationRequested)
                break;

            foreach (var s in syms)
            {
                var key = (s.Name ?? "", s.Location?.Uri ?? "", s.Location?.Range?.Start?.Line ?? 0);
                if (seen.Add(key))
                    merged.Add(s);
            }
        }

        return merged;
    }

    /// <summary>LSP シンボル 1 件を候補化する。選択でその定義（file:// URI → ローカルパス）の宣言行へジャンプ。
    /// 名前に加え、所属（<c>ContainerName</c>・名前空間や型）があれば併記する。</summary>
    private PaletteCommand SymbolEntry(LspSymbolInformation sym, string category)
    {
        var path = CodeEditorSupport.TryUriToLocalPath(sym.Location?.Uri);
        var line1 = (sym.Location?.Range?.Start?.Line ?? 0) + 1;
        var title = string.IsNullOrEmpty(sym.ContainerName) ? sym.Name : $"{sym.Name}  ·  {sym.ContainerName}";
        return new PaletteCommand(category, title,
            () => { if (path is not null) _ = OpenAndNavigateAsync(path, line1); })
        {
            PreviewPath = path,
            PreviewLine = line1,
        };
    }

    /// <summary>クラス／シンボル検索の状態行（空クエリ・未接続・一致なし）。実行アクションは持たない。</summary>
    private static PaletteCommand SymbolStatus(string text) => new("シンボル検索", text, static () => { });

    /// <summary>クラス相当（クラス／構造体／インターフェース／列挙体）の種別か。すべてモードでの重複除去に使う。</summary>
    private static bool IsClassKind(SymbolKind kind)
        => kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum;

    /// <summary>
    /// ターミナル内テキスト検索（$）。アクティブなターミナルタブのバッファから一致をすべて拾い、
    /// @（ファイル）／#（grep）と同じく候補一覧として並べる。選ぶとその箇所をターミナル上で
    /// 選択ハイライト＋スクロールしてジャンプする。一致が無い・ターミナルが無い場合は状態行だけ出す。
    /// </summary>
    private IReadOnlyList<PaletteCommand> BuildTerminalMatches(string query)
    {
        if (_activeTerminalTab?.View is not { } view)
            return new[] { TerminalStatus("ターミナルがありません") };

        if (string.IsNullOrWhiteSpace(query))
            return new[] { TerminalStatus("入力してターミナル内を検索") };

        var matches = view.FindMatches(query, caseSensitive: false);
        if (matches.Count == 0)
            return new[] { TerminalStatus("一致なし") };

        const int max = 200; // grep と同様に件数を上限で抑える
        return matches.Take(max).Select(m => TerminalMatchEntry(m, view)).ToList();
    }

    /// <summary>ターミナル一致1件を候補化する。選択で該当箇所へジャンプ（ハイライト＋スクロール）。</summary>
    private PaletteCommand TerminalMatchEntry(TerminalMatch match, TerminalTabView view)
        => new($"行 {match.LineIndex + 1}", match.LineText.Trim(), () =>
        {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
            view.SelectMatch(match);
            view.FocusTerminal();
        });

    /// <summary>ターミナル検索モードのリストに出す状態行（実行アクションは持たない）。</summary>
    private static PaletteCommand TerminalStatus(string text)
        => new("ターミナル検索", text, static () => { });

    private void ShowPaletteItems(IReadOnlyList<PaletteCommand> items)
    {
        // 一覧のタイトル強調用に、いま入力中の素のクエリを各項目へ添える（モード先頭記号は落とす）。
        var (_, query) = ParsePaletteMode(PaletteInput.Text);
        foreach (var item in items)
            item.TitleMatch = query;

        PaletteList.ItemsSource = items;
        if (PaletteList.Items.Count > 0)
        {
            PaletteList.SelectedIndex = 0;
            PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        }
        else
        {
            UpdatePalettePreview(null);
        }
    }

    private PaletteCommand FileEntry(FileSearchHit hit)
        => new("ファイル", hit.RelativePath, () => _ = OpenAndNavigateAsync(hit.FullPath, 0))
        { PreviewPath = hit.FullPath };

    private PaletteCommand GrepEntry(ContentSearchHit hit, string query)
        => new($"{hit.RelativePath}:{hit.Line}", hit.LineText.Trim(),
            () => _ = OpenAndNavigateAsync(hit.FullPath, hit.Line))
        { PreviewPath = hit.FullPath, PreviewLine = hit.Line, PreviewHighlight = query };

    /// <summary>ファイルをエディタタブで開き、行が指定されていればそこへジャンプする。</summary>
    private async Task OpenAndNavigateAsync(string path, int line)
    {
        await OpenFileInNewEditorTabAsync(path);
        if (line > 0 && _activeEditorTab?.Control is { } control)
            // line は1始まり、NavigateTo は0始まりなので変換する。
            control.NavigateTo(line - 1, 0);
    }

    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdatePalettePreview(PaletteList.SelectedItem as PaletteCommand);

    /// <summary>プレビュー用の読み取り専用エディタ（素の <see cref="VimEditorControl"/>）。エディタ本体と同じ
    /// 描画・シンタックスハイライト・全文スクロールをそのまま使う。1 個だけ作って使い回す。</summary>
    private VimEditorControl? _previewEditor;

    /// <summary>選択が高速に変わっても毎回ファイルを開かないよう、軽くデバウンスするためのトークン源。</summary>
    private CancellationTokenSource? _palettePreviewCts;

    /// <summary>プレビュー用エディタを（初回のみ）生成する。LSP/git ファクトリを渡さない「素」の構成なので
    /// 言語サーバー接続・git 差分・ファイル監視の副作用が無い。フォーカス不可にしてパレットの ↑↓ 操作を
    /// 奪わせない（<see cref="Editor.Controls.Rendering.EditorCanvas"/> はフォーカス無しでもホイールスクロール可）。
    /// 行番号とスクロールバーは残し、ステータスバーは切り離しの共有バーを渡して隠し、ミニマップは切る。
    /// 配色・フォントは本体エディタと揃える。</summary>
    private VimEditorControl EnsurePreviewEditor()
    {
        if (_previewEditor is { } existing)
            return existing;

        var editor = new VimEditorControl(new VimEditorControlOptions())
        {
            VimEnabled = false,
            Focusable = false,  // キーボードフォーカスを奪わない（↑↓・Enter はパレットのまま）
        };
        ApplyEditorOptions(editor);
        ApplyEditorAppearance(editor);
        editor.ExecuteCommand("set number");     // 行番号は常に表示（本体設定に依らず）
        editor.ExecuteCommand("set cursorline"); // ヒット行を常に強調
        editor.ExecuteCommand("set nominimap");  // 狭いプレビューではミニマップは邪魔なので切る
        // 表示しない共有ステータスバーを与えて内蔵ステータスバーだけ隠す（プレビューは非フォーカスなので
        // このバーへ状態は流れず、見た目にも出ない）。行番号ガターとスクロールバーはそのまま残る。
        editor.SetSharedStatusBar(new VimStatusBar());
        PalettePreviewHost.Child = editor;
        _previewEditor = editor;
        return editor;
    }

    /// <summary>選択中ヒットのファイルを、プレビュー用エディタで開いてヒット行へジャンプする。grep モードでは
    /// 検索語をエディタの検索ハイライトで重ねる。ファイル以外（コマンド等）を選んだときは隠す。</summary>
    private void UpdatePalettePreview(PaletteCommand? command)
    {
        _palettePreviewCts?.Cancel();

        if (command?.PreviewPath is not { } path || !File.Exists(path))
        {
            if (_previewEditor is not null)
                _previewEditor.Visibility = Visibility.Collapsed;
            return;
        }

        var cts = new CancellationTokenSource();
        _palettePreviewCts = cts;
        _ = ShowPalettePreviewAsync(command, path, cts.Token);
    }

    private async Task ShowPalettePreviewAsync(PaletteCommand command, string path, CancellationToken ct)
    {
        try
        {
            await Task.Delay(60, ct); // ↑↓ の連続移動でファイルを開きすぎないよう軽く待つ
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested)
            return;

        var editor = EnsurePreviewEditor();
        editor.Visibility = Visibility.Visible;
        try
        {
            editor.LoadFile(path);
            editor.HighlightSearch(command.PreviewHighlight ?? "");
            // 行が指定されていればその行をプレビューの中央に置き、無指定なら先頭から見せる。
            // 開いた直後はまだ Canvas が未計測で中央寄せ（JumpToLine）が効かないことがあるので、
            // レイアウトが確定する Background 優先度でもう一度合わせて確実に中央へ寄せる。
            NavigatePreview(editor, command);
            _ = editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (!ct.IsCancellationRequested && editor.Visibility == Visibility.Visible)
                    NavigatePreview(editor, command);
            });
        }
        catch
        {
            editor.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>プレビューを指定行へ移動する。行指定あり（grep／シンボル）は <see cref="VimEditorControl.JumpToLine"/>
    /// でその行を<b>ビューポート中央</b>に置き、行指定なし（ファイル）は先頭から見せる。
    /// PreviewLine は 1 始まり、JumpToLine／NavigateTo は 0 始まりなので変換する。</summary>
    private static void NavigatePreview(VimEditorControl editor, PaletteCommand command)
    {
        if (command.PreviewLine > 0)
            editor.JumpToLine(command.PreviewLine - 1, 0);
        else
            editor.NavigateTo(0, 0);
    }

    private void ExecutePaletteSelection()
    {
        if (PaletteList.SelectedItem is not PaletteCommand command)
            return;
        CloseCommandPalette(refocus: false);
        command.Execute();
    }

    private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => RefilterPalette();

    private void OnPaletteInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseCommandPalette(refocus: true);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecutePaletteSelection();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                MovePaletteSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    private void MovePaletteSelection(int delta)
    {
        var count = PaletteList.Items.Count;
        if (count == 0)
            return;
        PaletteList.SelectedIndex = ((PaletteList.SelectedIndex < 0 ? 0 : PaletteList.SelectedIndex)
            + delta + count) % count;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    /// <summary>背景（薄暗がり）クリックはキャンセル。</summary>
    private void OnPaletteBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        => CloseCommandPalette(refocus: true);

    /// <summary>パレット本体のクリックは背景まで抜けさせない。</summary>
    private void OnPaletteBoxMouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteCommand command })
        {
            e.Handled = true;
            CloseCommandPalette(refocus: false);
            command.Execute();
        }
    }

    /// <summary>現在状態からコマンド一覧を組む（開くたびに呼ぶ）。</summary>
    private List<PaletteCommand> BuildPaletteCommands()
    {
        var list = new List<PaletteCommand>();

        // カタログコマンドの実効ジェスチャ（再割り当てに追従）。
        string? Sc(string id) => _keybindings.For(id)?.Format();

        // ステージ
        list.Add(new("ステージ",
            _stageActive ? "ステージモードを解除（タイル表示へ）" : "ステージモードへ（舞台＋袖）",
            () => { if (_stageActive) ExitStageMode(); else EnterStageMode(); }));
        if (_stageActive)
            list.Add(new("ステージ", _overviewActive ? "俯瞰を閉じる" : "俯瞰（全カードを一望）",
                ToggleOverview, "Ctrl+W z"));

        // 移動（ステージ中は FocusPane がそのまま舞台転換になる）
        foreach (var kind in StageOrder)
        {
            var target = kind;
            list.Add(new("移動", $"{PaneLabel(target)} へ",
                () => { SetPaneVisible(target, true); FocusPane(target); }));
        }

        // ペイン表示
        foreach (var kind in StageOrder)
        {
            var target = kind;
            list.Add(new("ペイン", $"{PaneLabel(target)} の表示を切替",
                () => SetPaneVisible(target, !IsPaneVisible(target))));
        }

        // タブ
        list.Add(new("タブ", "新しいターミナルタブ", () => OnTerminalNewTab(this, new RoutedEventArgs()),
            Sc("tab.newTerminal"), "tab.newTerminal"));
        list.Add(new("タブ", "新しいエディタタブ", () => OnEditorNewTab(this, new RoutedEventArgs()),
            Sc("tab.newEditor"), "tab.newEditor"));
        list.Add(new("タブ", "新しいブラウザタブ", () => OnBrowserNewTab(this, new RoutedEventArgs()),
            Sc("tab.newBrowser"), "tab.newBrowser"));

        // コンポーザ（作業台）
        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く",
            () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, Sc("composer.run"), "composer.run"));
        list.Add(new("コンポーザ", "本文をペグボードへピン",
            () => OnComposerPinToPegboard(this, new RoutedEventArgs())));

        // ペグボード（道具掛け）
        list.Add(new("ペグボード", "クリップボードから追加",
            () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択をピン", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLをピン", PinBrowserUrlToPegboard));

        // サイドバー
        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null),
            Sc("sidebar.explorer"), "sidebar.explorer"));
        list.Add(new("サイドバー", "検索（全文検索 / grep）", () => _vm.ShowSearchCommand.Execute(null),
            Sc("sidebar.search"), "sidebar.search"));
        list.Add(new("サイドバー", "タブ一覧", () => _vm.ShowTabsCommand.Execute(null),
            Sc("sidebar.tabs"), "sidebar.tabs"));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null),
            Sc("sidebar.git"), "sidebar.git"));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null),
            Sc("sidebar.pegboard"), "sidebar.pegboard"));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null),
            Sc("sidebar.settings"), "sidebar.settings"));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null),
            Sc("sidebar.appearance"), "sidebar.appearance"));
        list.Add(new("サイドバー", "キーボード設定", () => _vm.ShowKeyboardSettingsCommand.Execute(null)));
        list.Add(new("サイドバー", "エクスプローラで現在のファイルを選択（同期）", RevealActiveFileInFolderTree,
            Sc("explorer.revealActiveFile"), "explorer.revealActiveFile"));

        // AI
        list.Add(new("AI", "AIセッション一覧を開閉", () => _vm.Sessions.ToggleOpenCommand.Execute(null),
            Sc("sidebar.sessions"), "sidebar.sessions"));

        // ワークスペース
        foreach (var workspace in _vm.Workspaces.Workspaces.Where(w => !w.IsActive))
        {
            var target = workspace;
            list.Add(new("ワークスペース", $"切替: {target.Name}",
                () => _vm.Workspaces.ActivateWorkspaceCommand.Execute(target)));
        }

        return list;
    }
}
