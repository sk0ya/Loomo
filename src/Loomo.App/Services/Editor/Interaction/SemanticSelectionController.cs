using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>selectionRange要求から選択範囲の拡大・縮小までを調整する。</summary>
internal sealed class SemanticSelectionController
{
    private readonly SemanticSelectionStack _stack = new();
    private int _generation;

    public int Depth => _stack.Depth;

    public async Task RunAsync(
        bool expand,
        Func<VimEditorControl?> focusedEditor,
        Action<string> showSharedStatus)
    {
        if (focusedEditor() is not { } control)
        {
            showSharedStatus("意味的な選択: エディタにフォーカスがありません。");
            return;
        }

        if (control.LspDocument is not { IsReady: true } document)
        {
            _stack.Reset();
            control.ShowStatusMessage("意味的な選択: 言語サーバーに接続していません。");
            return;
        }
        if (!document.ServerSupportsSelectionRange)
        {
            _stack.Reset();
            control.ShowStatusMessage("意味的な選択: この言語サーバーは selectionRange に対応していません。");
            return;
        }

        var uri = document.Uri;
        var revision = document.Version ?? 0;
        var caret = CaretSpan(control);
        var current = SelectionSpanOf(control);
        if (!_stack.IsUsableFor(control, uri, revision, current, caret))
        {
            if (!expand)
            {
                _stack.Reset();
                control.ShowStatusMessage("意味的な選択: 戻れる範囲がありません。");
                return;
            }
            if (!await BeginAsync(control, document, uri, revision, current, caret, focusedEditor))
                return;
        }

        if (expand) ExpandFromStack(control, current);
        else ShrinkFromStack(control);
    }

    public bool CanShrink(VimEditorControl control, ILspDocument document)
        => _stack.Depth > 0 && _stack.IsUsableFor(
            control, document.Uri, document.Version ?? 0,
            SelectionSpanOf(control), CaretSpan(control));

    private async Task<bool> BeginAsync(
        VimEditorControl control,
        ILspDocument document,
        string uri,
        int revision,
        SelectionSpan? current,
        SelectionSpan caret,
        Func<VimEditorControl?> focusedEditor)
    {
        var origin = current is { } selection
            ? SelectionSpan.At(selection.StartLine, selection.StartColumn)
            : caret;
        var generation = ++_generation;
        _stack.Reset();

        LspSelectionRange? chain;
        try { chain = await document.RequestSelectionRangeAsync(origin.StartLine, origin.StartColumn); }
        catch { chain = null; }

        if (generation != _generation) return false;
        if (focusedEditor() is { } focusedNow && !ReferenceEquals(focusedNow, control)) return false;
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
        _stack.Begin(control, uri, revision, spans, caret);
        return true;
    }

    private void ExpandFromStack(VimEditorControl control, SelectionSpan? current)
    {
        if (_stack.NextExpansion(current) is not { } next)
        {
            control.ShowStatusMessage("意味的な選択: これ以上広げられません。");
            return;
        }
        Apply(control, next);
        if (SelectionSpanOf(control) is not { } observed)
        {
            _stack.Reset();
            control.ShowStatusMessage("意味的な選択: 範囲を選択できませんでした。");
            return;
        }
        _stack.Push(next, observed);
        control.ShowStatusMessage($"意味的な選択: {_stack.Depth} 段目");
    }

    private void ShrinkFromStack(VimEditorControl control)
    {
        if (_stack.Shrink() is not { } back)
        {
            control.ShowStatusMessage("意味的な選択: これ以上縮められません。");
            return;
        }
        Apply(control, back);
        control.ShowStatusMessage(_stack.Depth == 0
            ? "意味的な選択: 起点へ戻りました"
            : $"意味的な選択: {_stack.Depth} 段目");
    }

    private static void Apply(VimEditorControl control, SelectionSpan span)
        => control.SelectRange(span.StartLine, span.StartColumn, span.EndLine, span.EndColumn);

    private static SelectionSpan CaretSpan(VimEditorControl control)
        => SelectionSpan.At(control.Caret.Line, control.Caret.Column);

    private static SelectionSpan? SelectionSpanOf(VimEditorControl control)
        => control.SelectionAsLspRange() is { } range ? SelectionSpan.From(range) : null;
}
