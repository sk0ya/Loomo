using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 意味的な選択の拡大／縮小（設計書 §24.9）の判断。ここが緩むと「縮小したらまったく関係ない範囲が
/// 選ばれる」という一番不快な壊れ方をするので、<b>どこまで広げるか</b>と<b>いつ来た道を捨てるか</b>の
/// 2つを、エディタにも LSP にも触れずに確かめられるところまで切り出してある。
/// </summary>
public class SemanticSelectionTests
{
    private static SelectionSpan Span(int startLine, int startColumn, int endLine, int endColumn)
        => new(startLine, startColumn, endLine, endColumn);

    /// <summary>内側→外側の並びから <c>Parent</c> 連鎖を組む（サーバー応答の形）。</summary>
    private static LspSelectionRange? Chain(params SelectionSpan[] innerToOuter)
    {
        LspSelectionRange? node = null;
        for (int i = innerToOuter.Length - 1; i >= 0; i--)
        {
            var span = innerToOuter[i];
            node = new LspSelectionRange(
                new LspRange(
                    new LspPosition(span.StartLine, span.StartColumn),
                    new LspPosition(span.EndLine, span.EndColumn)),
                node);
        }
        return node;
    }

    // ───────────────────────── 連鎖から次の範囲を選ぶ ─────────────────────────

    [Fact]
    public void 連鎖の先頭が現在の選択と同じなら飛ばして真に大きい祖先を選ぶ()
    {
        // サーバーは「キャレットを含む最も内側」から返すので、すでにその範囲を選んでいると
        // 先頭は現在の選択そのもの。ここで「含む」で選ぶと同じ範囲を選び直して何も起きない。
        var chain = SemanticSelectionChain.Flatten(Chain(
            Span(1, 14, 1, 15),   // 識別子（＝いまの選択）
            Span(1, 14, 1, 17),   // 呼び出し式
            Span(1, 2, 1, 19)));  // 文

        var next = SemanticSelectionChain.FindExpansion(chain, Span(1, 14, 1, 15));

        Assert.Equal(Span(1, 14, 1, 17), next);
    }

    [Fact]
    public void 連鎖の先頭が現在の選択の内側なら飛ばす()
    {
        // 手で「呼び出し式より広く、文より狭い」範囲を選んでから広げた場合。
        // 先頭2段は現在の選択の内側なので、どちらも「広げた」ことにならない。
        var chain = SemanticSelectionChain.Flatten(Chain(
            Span(1, 14, 1, 15),
            Span(1, 14, 1, 17),
            Span(1, 2, 1, 19),
            Span(0, 0, 2, 1)));

        var next = SemanticSelectionChain.FindExpansion(chain, Span(1, 13, 1, 18));

        Assert.Equal(Span(1, 2, 1, 19), next);
    }

    [Fact]
    public void 選択が無ければ連鎖の先頭_最も内側_を選ぶ()
    {
        var chain = SemanticSelectionChain.Flatten(Chain(
            Span(1, 14, 1, 15),
            Span(1, 14, 1, 17)));

        Assert.Equal(Span(1, 14, 1, 15), SemanticSelectionChain.FindExpansion(chain, current: null));
    }

    [Fact]
    public void これ以上広げられなければnullを返す()
    {
        var chain = SemanticSelectionChain.Flatten(Chain(
            Span(1, 14, 1, 15),
            Span(0, 0, 2, 1)));

        Assert.Null(SemanticSelectionChain.FindExpansion(chain, Span(0, 0, 2, 1)));
    }

    // ───────────────────────── 退化した連鎖 ─────────────────────────

    [Fact]
    public void 空の連鎖は段を作らない()
    {
        Assert.Empty(SemanticSelectionChain.Flatten(null));
        Assert.Null(SemanticSelectionChain.FindExpansion(SemanticSelectionChain.Flatten(null), null));
        Assert.Null(SemanticSelectionChain.FindExpansion(SemanticSelectionChain.Flatten(null), Span(1, 0, 1, 4)));
    }

    [Fact]
    public void Parentが無く範囲も空の連鎖は段を作らない()
    {
        // 空白の上など、サーバーが1点だけを返すことがある。1点は選択にならないので段にしない。
        var chain = SemanticSelectionChain.Flatten(Chain(Span(3, 5, 3, 5)));

        Assert.Empty(chain);
        Assert.Null(SemanticSelectionChain.FindExpansion(chain, null));
    }

    [Fact]
    public void 同じ範囲が続く連鎖は1段に畳む()
    {
        // 「式」と「文」が同じ範囲になる言語・位置がある。段として残すと押しても何も起きない段になる。
        var chain = SemanticSelectionChain.Flatten(Chain(
            Span(1, 14, 1, 15),
            Span(1, 2, 1, 19),
            Span(1, 2, 1, 19),
            Span(0, 0, 2, 1)));

        Assert.Equal(3, chain.Count);
        Assert.Equal(new[] { Span(1, 14, 1, 15), Span(1, 2, 1, 19), Span(0, 0, 2, 1) }, chain);
    }

    [Fact]
    public void 空の範囲が途中に混ざっても段にしない()
    {
        var chain = SemanticSelectionChain.Flatten(Chain(
            Span(1, 14, 1, 14),
            Span(1, 14, 1, 15),
            Span(0, 0, 2, 1)));

        Assert.Equal(new[] { Span(1, 14, 1, 15), Span(0, 0, 2, 1) }, chain);
    }

    // ───────────────────────── 来た道（スタック） ─────────────────────────

    /// <summary>
    /// エディタ側の変換をそのまま真似た代役。<c>SelectRange</c> は終端を含まない半開区間
    /// <c>[start, end)</c> として受け取り、内部では<b>終端を含むセル範囲</b>で保持し、
    /// <c>SelectionAsLspRange()</c> でまた終端を含まない形へ戻す——この往復は<b>恒等ではない</b>。
    /// </summary>
    private sealed class FakeEditorSelection(IReadOnlyList<int> lineLengths)
    {
        private SelectionSpan? _selection;
        public SelectionSpan Caret { get; private set; }

        public void SelectRange(SelectionSpan span)
        {
            Caret = SelectionSpan.At(span.StartLine, span.StartColumn);
            _selection = span.IsEmpty ? null : span;
        }

        /// <summary>いまの選択を LSP の range として読み戻す。</summary>
        public SelectionSpan? Read()
        {
            if (_selection is not { } span) return null;

            // 終端を含まない境界 → 終端を含むセル
            var (endLine, endColumn) = span.EndColumn > 0
                ? (span.EndLine, span.EndColumn - 1)
                : span.EndLine > 0
                    ? (span.EndLine - 1, lineLengths[span.EndLine - 1])
                    : (span.StartLine, span.StartColumn);

            // 終端を含むセル → 終端を含まない境界（行末を越えない）
            return new SelectionSpan(
                span.StartLine, span.StartColumn,
                endLine, Math.Min(endColumn + 1, lineLengths[endLine]));
        }
    }

    /// <summary>行の長さ（<c>class C {</c> / <c>  void M() { X(); }</c> / <c>}</c> を想定）。</summary>
    private static readonly int[] LineLengths = [9, 19, 1];

    private static readonly SelectionSpan Identifier = Span(1, 14, 1, 15);   // X
    private static readonly SelectionSpan CallExpr = Span(1, 14, 1, 17);     // X()
    private static readonly SelectionSpan Method = Span(1, 2, 1, 19);        // void M() { X(); }
    private static readonly SelectionSpan Members = Span(0, 9, 2, 0);        // 行頭で終わる（往復が恒等でない）
    private static readonly SelectionSpan Klass = Span(0, 0, 2, 1);          // class C { … }

    private static SelectionSpan[] SampleChain => [Identifier, CallExpr, Method, Members, Klass];

    private static (SemanticSelectionStack Stack, object Owner, FakeEditorSelection Editor) NewSession(
        SelectionSpan caret, int revision = 1)
    {
        var stack = new SemanticSelectionStack();
        var owner = new object();
        var editor = new FakeEditorSelection(LineLengths);
        stack.Begin(owner, "file:///c:/work/a.cs", revision, SampleChain, caret);
        return (stack, owner, editor);
    }

    /// <summary>拡大1段ぶんの適用（ホストがやることと同じ手順）。</summary>
    private static void Expand(SemanticSelectionStack stack, FakeEditorSelection editor)
    {
        var next = stack.NextExpansion(editor.Read());
        Assert.NotNull(next);
        editor.SelectRange(next.Value);
        stack.Push(next.Value, editor.Read()!.Value);
    }

    [Fact]
    public void 拡大と縮小で来た道をそのまま戻る()
    {
        var caret = SelectionSpan.At(1, 14);
        var (stack, _, editor) = NewSession(caret);

        Expand(stack, editor);
        Assert.Equal(Identifier, editor.Read());
        Expand(stack, editor);
        Assert.Equal(CallExpr, editor.Read());
        Assert.Equal(2, stack.Depth);

        // 縮小は来た道をそのまま戻る（連鎖を辿り直すのではなく、積んだ範囲へ戻す）。
        var back = stack.Shrink();
        Assert.Equal(Identifier, back);
        editor.SelectRange(back!.Value);
        Assert.Equal(Identifier, editor.Read());
        Assert.Equal(1, stack.Depth);

        // もう1段戻すと起点のキャレットへ。選択は消える。
        var origin = stack.Shrink();
        Assert.Equal(caret, origin);
        editor.SelectRange(origin!.Value);
        Assert.Null(editor.Read());
        Assert.Equal(0, stack.Depth);
        Assert.Equal(caret, editor.Caret);
    }

    [Fact]
    public void 段を1つも積んでいなければ縮小できない()
    {
        var (stack, _, _) = NewSession(SelectionSpan.At(1, 14));

        Assert.Null(stack.Shrink());
    }

    [Fact]
    public void 読み戻した範囲が適用した範囲と違っても次の祖先を取り違えない()
    {
        // Members は行頭 (2,0) で終わるので、エディタから読み戻すと前の行の行末 (1,19) になる。
        // 「読み戻した形」を基準に祖先を探すと、Members 自身が (1,19) を真に含むので
        // <b>同じ段をもう一度選ぶ</b>——押しても選択が変わらない、という壊れ方になる。
        var (stack, _, editor) = NewSession(SelectionSpan.At(0, 9));

        for (int i = 0; i < 4; i++) Expand(stack, editor);   // 識別子→呼び出し→メソッド→メンバー

        Assert.Equal(4, stack.Depth);
        Assert.Equal(Span(0, 9, 1, 19), editor.Read());      // 読み戻すと形が変わっている
        Assert.NotEqual(Members, editor.Read());

        // 次は class まで飛ぶ（Members をもう一度選ばない）。
        Assert.Equal(Klass, stack.NextExpansion(editor.Read())!.Value);
    }

    // ───────────────────────── 来た道を捨てる条件 ─────────────────────────

    [Fact]
    public void 同じ文書_同じ版_同じ選択なら地続き()
    {
        var (stack, owner, editor) = NewSession(SelectionSpan.At(1, 14));
        Expand(stack, editor);

        Assert.True(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, editor.Read(), editor.Caret));
    }

    [Fact]
    public void 文書が変わったらスタックを捨てる()
    {
        var (stack, owner, editor) = NewSession(SelectionSpan.At(1, 14));
        Expand(stack, editor);

        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/b.cs", 1, editor.Read(), editor.Caret));
    }

    [Fact]
    public void 本文が編集されたら_文書の版が進んだら_スタックを捨てる()
    {
        var (stack, owner, editor) = NewSession(SelectionSpan.At(1, 14));
        Expand(stack, editor);

        // 選択そのものは同じに見えても、本文が変われば同じ範囲が別の意味になる。
        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 2, editor.Read(), editor.Caret));
    }

    [Fact]
    public void 別のエディタビューならスタックを捨てる()
    {
        // 分割ビューでは同じファイルが2枚出る。URI も版も同じなので、ビューで見分けないと
        // 片方で広げた記憶がもう片方の縮小に流れ込む。
        var (stack, _, editor) = NewSession(SelectionSpan.At(1, 14));
        Expand(stack, editor);

        Assert.False(stack.IsUsableFor(new object(), "file:///c:/work/a.cs", 1, editor.Read(), editor.Caret));
    }

    [Fact]
    public void 利用者が選択を動かしたらスタックを捨てる()
    {
        var (stack, owner, editor) = NewSession(SelectionSpan.At(1, 14));
        Expand(stack, editor);

        // 手で選び直した
        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, Span(1, 4, 1, 8), editor.Caret));
        // 選択を解いた（キャレットだけ動かした）
        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, null, SelectionSpan.At(1, 4)));
    }

    [Fact]
    public void 起点へ戻った状態は選択が無くキャレットが起点のときだけ地続き()
    {
        var caret = SelectionSpan.At(1, 14);
        var (stack, owner, editor) = NewSession(caret);
        Expand(stack, editor);
        editor.SelectRange(stack.Shrink()!.Value);   // 起点へ戻す

        Assert.Equal(0, stack.Depth);
        Assert.True(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, null, caret));

        // キャレットが起点から動いていれば、もう地続きではない（問い合わせ直す）。
        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, null, SelectionSpan.At(1, 3)));
    }

    [Fact]
    public void 捨てたあとは何も覚えていない()
    {
        var (stack, owner, editor) = NewSession(SelectionSpan.At(1, 14));
        Expand(stack, editor);

        stack.Reset();

        Assert.Equal(0, stack.Depth);
        Assert.Empty(stack.Chain);
        Assert.Null(stack.Shrink());
        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, editor.Read(), editor.Caret));
    }

    [Fact]
    public void 連鎖を覚えていなければ地続きにはならない()
    {
        // Begin を通っていない（＝サーバー応答を持っていない）まっさらなスタック。
        var stack = new SemanticSelectionStack();
        var owner = new object();

        Assert.False(stack.IsUsableFor(owner, "file:///c:/work/a.cs", 1, null, SelectionSpan.At(0, 0)));
        Assert.Null(stack.NextExpansion(null));
    }
}
