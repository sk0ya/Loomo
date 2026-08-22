namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 意味的な選択の拡大／縮小（設計書 §24.9）。
///
/// <para>キャレットから「識別子 → 式 → 引数リスト → 文 → ブロック → メソッド → クラス」と
/// <b>構文木に沿って</b>選択を広げ、縮小で来た道をそのまま戻る。範囲は言語サーバーの
/// <c>textDocument/selectionRange</c> が返す連鎖（先頭＝最も内側、<c>Parent</c> を辿るほど外側）で、
/// 問い合わせ口は <c>LspDocumentHandle.RequestSelectionRangeAsync</c>（§30 のとおり LSP セッションの
/// 所有者は Loomo）。ここは <c>ShellWindow.Refactoring</c> と同じ立場のホスト側の消費者。</para>
///
/// <para><b>来た道は <see cref="SemanticSelectionStack"/> が覚える。</b>拡大のたびに適用した範囲を積み、
/// 縮小はそこから戻す。捨てる条件（文書が変わった・本文が編集された・利用者が選択やキャレットを
/// 動かした）は全部あちら側にあり、ここはエディタから材料を読んで渡すだけ。</para>
///
/// <para><b>使えないときは黙らない。</b>LSP 未接続・サーバーが <c>selectionRange</c> 未対応・
/// これ以上広げられない／縮められない、はすべてステータスへ理由を出す。</para></summary>
public partial class ShellWindow
{
    /// <summary>来た道。ウィンドウにつき1つで足りる——対象は「いまフォーカスされているエディタ」1枚だけで、
    /// 別のビューへ移った時点でこの記憶は捨てる対象になる（<c>IsUsableFor</c> の owner 判定）。</summary>
    private readonly SemanticSelectionStack _semanticSelection = new();

    /// <summary>問い合わせの世代（設計書 §31.2 原則5）。応答を待つ間に文書やキャレットが動きうるので、
    /// 古い応答で選択を書き換えない。</summary>
    private int _semanticSelectionGeneration;

    private void ExpandSemanticSelection() => _ = RunSemanticSelectionAsync(expand: true);
    private void ShrinkSemanticSelection() => _ = RunSemanticSelectionAsync(expand: false);

    /// <summary>拡大／縮小の本体。対象は<b>フォーカスされているエディタ</b>（分割ビューでも1枚に決まる）。</summary>
    private async Task RunSemanticSelectionAsync(bool expand)
    {
        if (FocusedEditorControl() is not { } control)
        {
            // パレットから呼ばれてエディタが1枚も無い／別のペインにフォーカスがある場合。
            EditorSharedStatusBar?.UpdateStatus("意味的な選択: エディタにフォーカスがありません。");
            return;
        }

        if (control.LspDocument is not { IsReady: true } document)
        {
            _semanticSelection.Reset();
            control.ShowStatusMessage("意味的な選択: 言語サーバーに接続していません。");
            return;
        }
        if (!document.ServerSupportsSelectionRange)
        {
            _semanticSelection.Reset();
            control.ShowStatusMessage("意味的な選択: この言語サーバーは selectionRange に対応していません。");
            return;
        }

        var uri = document.Uri;
        var revision = document.Version ?? 0;
        var caret = CaretSpan(control);
        var current = SelectionSpanOf(control);

        if (!_semanticSelection.IsUsableFor(control, uri, revision, current, caret))
        {
            // 来た道が無い状態の縮小は<b>何もしない</b>。ここで適当な範囲へ縮めると、
            // 「縮小したらまったく関係ない範囲が選ばれる」という一番不快な壊れ方になる。
            if (!expand)
            {
                _semanticSelection.Reset();
                control.ShowStatusMessage("意味的な選択: 戻れる範囲がありません。");
                return;
            }
            if (!await BeginSemanticSelectionAsync(control, document, uri, revision, current, caret)) return;
        }

        if (expand) ExpandFromStack(control, current);
        else ShrinkFromStack(control);
    }

    /// <summary>連鎖をサーバーから取り直して覚え直す。取れなければ false（呼び出し側は何もしない）。</summary>
    private async Task<bool> BeginSemanticSelectionAsync(
        VimEditorControl control, ILspDocument document,
        string uri, int revision, SelectionSpan? current, SelectionSpan caret)
    {
        // 起点は「選択があればその先頭、無ければキャレット」。選択の先頭にすると、
        // 手で選んでから広げたときも構文木の同じ枝から辿れる。
        var origin = current is { } selection
            ? SelectionSpan.At(selection.StartLine, selection.StartColumn)
            : caret;

        var generation = ++_semanticSelectionGeneration;
        _semanticSelection.Reset();

        Editor.Core.Lsp.LspSelectionRange? chain;
        try { chain = await document.RequestSelectionRangeAsync(origin.StartLine, origin.StartColumn); }
        catch { chain = null; }

        // 世代管理（§31.2 原則5）。待っている間に別のビューへ移った・文書が変わった・本文が編集された・
        // キャレットや選択が動いた、のどれかなら<b>この応答は捨てる</b>。
        if (generation != _semanticSelectionGeneration) return false;
        // 「別のエディタへ移った」なら捨てる。フォーカスがエディタから外れているだけ（パレット経由の
        // 呼び出し直後など）は捨てない——呼び出した本人のビューはそのままそこにいる。
        if (FocusedEditorControl() is { } focusedNow && !ReferenceEquals(focusedNow, control)) return false;
        if (control.LspDocument is not { } now
            || !string.Equals(now.Uri, uri, StringComparison.Ordinal)
            || (now.Version ?? 0) != revision) return false;
        if (SelectionSpanOf(control) != current || CaretSpan(control) != caret) return false;

        var spans = SemanticSelectionChain.Flatten(chain);
        if (spans.Count == 0)
        {
            control.ShowStatusMessage("意味的な選択: この位置では範囲が返りませんでした。");
            return false;
        }

        _semanticSelection.Begin(control, uri, revision, spans, caret);
        return true;
    }

    private void ExpandFromStack(VimEditorControl control, SelectionSpan? current)
    {
        if (_semanticSelection.NextExpansion(current) is not { } next)
        {
            control.ShowStatusMessage("意味的な選択: これ以上広げられません。");
            return;
        }

        ApplySemanticSelection(control, next);

        // 適用した範囲と読み戻した範囲は一致しないことがある（行頭終わりの範囲）。
        // 「利用者が動かしたか」は読み戻した形で判定するので、両方を積む。
        if (SelectionSpanOf(control) is not { } observed)
        {
            _semanticSelection.Reset();
            control.ShowStatusMessage("意味的な選択: 範囲を選択できませんでした。");
            return;
        }
        _semanticSelection.Push(next, observed);
        control.ShowStatusMessage($"意味的な選択: {_semanticSelection.Depth} 段目");
    }

    private void ShrinkFromStack(VimEditorControl control)
    {
        if (_semanticSelection.Shrink() is not { } back)
        {
            control.ShowStatusMessage("意味的な選択: これ以上縮められません。");
            return;
        }

        ApplySemanticSelection(control, back);
        control.ShowStatusMessage(_semanticSelection.Depth == 0
            ? "意味的な選択: 起点へ戻りました"
            : $"意味的な選択: {_semanticSelection.Depth} 段目");
    }

    /// <summary>範囲をエディタへ適用する。<see cref="VimEditorControl.SelectRange"/> の終端は
    /// <b>含まない</b>境界として解釈されるので（内部では [start, end) の半開区間）、LSP の range を
    /// そのまま渡してよい。起点（空の範囲）を渡した場合は選択が消えてキャレットだけが残る。</summary>
    private static void ApplySemanticSelection(VimEditorControl control, SelectionSpan span)
        => control.SelectRange(span.StartLine, span.StartColumn, span.EndLine, span.EndColumn);

    private static SelectionSpan CaretSpan(VimEditorControl control)
        => SelectionSpan.At(control.Caret.Line, control.Caret.Column);

    private static SelectionSpan? SelectionSpanOf(VimEditorControl control)
        => control.SelectionAsLspRange() is { } range ? SelectionSpan.From(range) : null;

    /// <summary>いまキーボードフォーカスを持っているエディタ。分割ビュー・複数タブでも1枚に決まる。
    /// フォーカスが視覚木から辿れないとき（フォーカスがまだ実要素に載っていない直後など）だけ、
    /// エディタペインが選ばれている場合に限って分割の選択葉へ落とす。</summary>
    private VimEditorControl? FocusedEditorControl()
    {
        for (var node = Keyboard.FocusedElement as DependencyObject; node is not null; node = AnyParent(node))
            if (node is VimEditorControl focused) return focused;

        if (_focusedRegion is not { Pane: PaneKind.Editor }) return null;
        if (_editorViews?.FocusedTabId is { } id
            && _editorTabs.FirstOrDefault(t => t.Id == id) is { IsRealized: true } tab)
            return tab.Control;
        return _activeEditorTab is { IsRealized: true } active ? active.Control : null;

        static DependencyObject? AnyParent(DependencyObject d)
            => d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
    }

    /// <summary>右クリックメニューへ拡大／縮小を足す（§31.2 原則6＝パレット・キー・UI から同じ実装へ）。
    /// 「縮小」は<b>戻れる段があるときだけ</b>出す——押しても何も起きない項目は並べない。
    /// エディタは選択の内側での右クリックでは選択もキャレットも動かさないので、
    /// 広げてから右クリックすれば縮小はそのまま使える。</summary>
    private void AddSemanticSelectionMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (control?.LspDocument is not { IsReady: true, ServerSupportsSelectionRange: true } document) return;

        bool canShrink = _semanticSelection.Depth > 0
            && _semanticSelection.IsUsableFor(
                control, document.Uri, document.Version ?? 0,
                SelectionSpanOf(control), CaretSpan(control));

        menu.Items.Add(new Separator());
        var expand = new MenuItem
        {
            Header = "選択を意味的に広げる",
            InputGestureText = DescribeBinding("editor.selection.expand"),
        };
        expand.Click += (_, _) => _ = RunSemanticSelectionAsync(expand: true);
        menu.Items.Add(expand);

        if (!canShrink) return;
        var shrink = new MenuItem
        {
            Header = "選択を1段戻す",
            InputGestureText = DescribeBinding("editor.selection.shrink"),
        };
        shrink.Click += (_, _) => _ = RunSemanticSelectionAsync(expand: false);
        menu.Items.Add(shrink);
    }

    /// <summary>コマンド Id の実効キー表記（未割当なら空文字＝ジェスチャ欄を出さない）。</summary>
    private string DescribeBinding(string commandId)
        => _keybindings.Effective.TryGetValue(commandId, out var sequence) ? sequence.ToString() : "";
}
