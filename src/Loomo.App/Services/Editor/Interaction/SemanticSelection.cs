using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 0 始まり・<b>終端を含まない</b>テキスト範囲（LSP の <see cref="LspRange"/> と同じ規約）。
/// 「意味的な選択」（設計書 §24.9）の判断は、エディタの内部表現（終端を含むセル範囲）ではなく
/// この規約1つで通す——変換が2種類あると、同じ選択が場所によって別物に見える。
/// </summary>
public readonly record struct SelectionSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public static SelectionSpan From(LspRange range) => new(
        range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);

    /// <summary>キャレット1点（起点）を表す空の範囲。</summary>
    public static SelectionSpan At(int line, int column) => new(line, column, line, column);

    public bool IsEmpty => StartLine == EndLine && StartColumn == EndColumn;

    /// <summary>この範囲が <paramref name="inner"/> を含む（同一も含む）。</summary>
    public bool Contains(SelectionSpan inner)
        => Compare(StartLine, StartColumn, inner.StartLine, inner.StartColumn) <= 0
        && Compare(EndLine, EndColumn, inner.EndLine, inner.EndColumn) >= 0;

    /// <summary>同一ではなく<b>真に</b>含む。拡大先はこれを満たすものだけを選ぶ——
    /// サーバーの返す連鎖の先頭は現在の選択と同じ（または内側）でありうるので、
    /// 「含む」で選ぶと同じ範囲を選び直して押しても何も起きない状態になる。</summary>
    public bool StrictlyContains(SelectionSpan inner) => !Equals(inner) && Contains(inner);

    private static int Compare(int line, int column, int otherLine, int otherColumn)
        => line != otherLine ? line.CompareTo(otherLine) : column.CompareTo(otherColumn);
}

/// <summary>拡大1段ぶんの記録。</summary>
/// <param name="Applied">サーバーの連鎖から選んで適用した範囲。<b>次の祖先を探す基準はこちら</b>。</param>
/// <param name="Observed">適用直後にエディタが返した範囲。<b>利用者が動かしたかの判定はこちら</b>。
/// 2つ持つのは、適用した範囲と読み戻した範囲が<b>一致しないことがある</b>ため（§24.9 の罠）。
/// 行頭で終わる範囲 <c>(el,0)</c> を選択すると、エディタは終端を含むセル範囲へ直して保持し、
/// 読み戻しでは前の行の行末 <c>(el-1,len)</c> として返る。1つに畳むと、来た道の一致判定か
/// 祖先探しのどちらかが必ず壊れる。</param>
public readonly record struct SemanticSelectionStep(SelectionSpan Applied, SelectionSpan Observed);

/// <summary>
/// <c>textDocument/selectionRange</c> が返す連鎖（先頭＝最も内側、<c>Parent</c> を辿るほど外側）から、
/// 「いまより真に大きい最初の範囲」を選ぶ純粋な部分。
/// </summary>
public static class SemanticSelectionChain
{
    /// <summary>連鎖を辿れる上限。壊れたサーバーが <c>Parent</c> を循環させても止まるようにする。</summary>
    private const int MaxDepth = 256;

    /// <summary>連鎖を内側→外側の並びへ均す。空の範囲（1点）と、連続する同一段は落とす——
    /// どちらも「広げた」ことにならないので、段として持つと空押しが発生する。</summary>
    public static IReadOnlyList<SelectionSpan> Flatten(LspSelectionRange? chain)
    {
        var spans = new List<SelectionSpan>();
        var node = chain;
        for (int depth = 0; node is not null && depth < MaxDepth; node = node.Parent, depth++)
        {
            var range = node.Range;
            if (range is null || range.Start is null || range.End is null) continue;
            var span = SelectionSpan.From(range);
            if (span.IsEmpty) continue;
            if (spans.Count > 0 && spans[^1] == span) continue;
            spans.Add(span);
        }
        return spans;
    }

    /// <summary>
    /// <paramref name="current"/> を真に含む最初の範囲。<paramref name="current"/> が null
    /// （＝選択が無くキャレットが起点）なら連鎖の先頭＝最も内側。これ以上広げられなければ null。
    /// </summary>
    public static SelectionSpan? FindExpansion(IReadOnlyList<SelectionSpan> chain, SelectionSpan? current)
    {
        if (current is not { } inner)
            return chain.Count > 0 ? chain[0] : null;

        foreach (var span in chain)
            if (span.StrictlyContains(inner))
                return span;
        return null;
    }
}

/// <summary>
/// 「意味的な選択」の<b>来た道</b>（設計書 §24.9）。拡大のたびに適用した範囲を積み、縮小はそこから戻す。
///
/// <para>このスタックが信用できなくなる条件を1か所に集めるのがこのクラスの役目
/// （<see cref="IsUsableFor"/>）。ここが緩むと<b>縮小したらまったく関係ない範囲が選ばれる</b>という
/// 一番不快な壊れ方をする。捨てるのは、対象の文書（ビュー／URI）が変わったとき、本文が編集されたとき
/// （文書の版が進んだとき）、そして利用者が自分で選択やキャレットを動かしたとき
/// （＝いまの選択が積んだ一番上と一致しないとき）。</para>
///
/// <para>連鎖（サーバー応答）も一緒に持つ。同じ起点・同じ本文なら答えは変わらないので、
/// 2段目以降の拡大でサーバーへ往復しない。</para>
/// </summary>
public sealed class SemanticSelectionStack
{
    private readonly List<SemanticSelectionStep> _steps = [];
    private object? _owner;
    private string _documentUri = "";
    private int _revision;
    private SelectionSpan _anchor;
    private IReadOnlyList<SelectionSpan> _chain = [];

    /// <summary>いま何段広げているか。0 は起点（連鎖は持っているが未適用）。</summary>
    public int Depth => _steps.Count;

    /// <summary>覚えている連鎖（内側→外側）。</summary>
    public IReadOnlyList<SelectionSpan> Chain => _chain;

    /// <summary>起点のキャレット（空の範囲）。全部縮小するとここへ戻る。</summary>
    public SelectionSpan Anchor => _anchor;

    /// <summary>
    /// いまの状態がこのスタックの記憶と地続きか。false なら記憶は捨てて問い合わせ直す。
    /// </summary>
    /// <param name="owner">対象のエディタビュー（分割ごとに別物として扱う）。</param>
    /// <param name="documentUri">対象文書の URI。</param>
    /// <param name="revision">本文の版（LSP 文書の版＝<c>didChange</c> のたびに進む）。</param>
    /// <param name="currentSelection">いまの選択（無ければ null）。</param>
    /// <param name="caret">いまのキャレット（空の範囲）。</param>
    public bool IsUsableFor(
        object owner, string documentUri, int revision,
        SelectionSpan? currentSelection, SelectionSpan caret)
    {
        if (_owner is null || !ReferenceEquals(_owner, owner)) return false;
        if (!string.Equals(_documentUri, documentUri, StringComparison.Ordinal)) return false;
        if (_revision != revision) return false;
        if (_chain.Count == 0) return false;

        // 1段も積んでいない＝起点にいる。選択が無く、キャレットが起点のままなら地続き。
        if (_steps.Count == 0) return currentSelection is null && caret == _anchor;

        return currentSelection is { } current && current == _steps[^1].Observed;
    }

    /// <summary>新しい起点で覚え直す。</summary>
    public void Begin(
        object owner, string documentUri, int revision,
        IReadOnlyList<SelectionSpan> chain, SelectionSpan anchor)
    {
        Reset();
        _owner = owner;
        _documentUri = documentUri;
        _revision = revision;
        _chain = chain;
        _anchor = anchor;
    }

    public void Reset()
    {
        _steps.Clear();
        _owner = null;
        _documentUri = "";
        _revision = 0;
        _chain = [];
        _anchor = default;
    }

    /// <summary>
    /// 次に広げるべき範囲。すでに積んでいるなら<b>積んだ範囲</b>を基準に探す——
    /// 読み戻した範囲を基準にすると、行頭終わりの範囲で「同じ段」を選び直してしまう
    /// （<see cref="SemanticSelectionStep.Observed"/> の説明）。これ以上広げられなければ null。
    /// </summary>
    public SelectionSpan? NextExpansion(SelectionSpan? currentSelection)
        => SemanticSelectionChain.FindExpansion(
            _chain, _steps.Count > 0 ? _steps[^1].Applied : currentSelection);

    public void Push(SelectionSpan applied, SelectionSpan observed)
        => _steps.Add(new SemanticSelectionStep(applied, observed));

    /// <summary>1段戻す。戻った先に適用すべき範囲（もう段が無ければ起点のキャレット＝空の範囲）。
    /// 1段も積んでいなければ null（＝縮小できない）。</summary>
    public SelectionSpan? Shrink()
    {
        if (_steps.Count == 0) return null;
        _steps.RemoveAt(_steps.Count - 1);
        return _steps.Count > 0 ? _steps[^1].Applied : _anchor;
    }
}
