namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 「直前のペインへ」（<c>pane.focus.previous</c>・§24.58）のための、いまと1つ前のフォーカス位置。
/// 同じ位置の中でフォーカスが動いても（エディタの中・ツリーの行どうし）前の位置は変えない——Vim の
/// <c>wincmd p</c> と同じく「別の場所から来た、その場所」だけを覚える。要素は弱参照で持つ（閉じた面を生かさない）。
/// </summary>
internal sealed class PaneFocusHistory<TElement> where TElement : class
{
    private (PaneFocusTarget Target, WeakReference<TElement> Element)? _current;

    /// <summary>いまの位置に来る直前に居た位置と、そこで最後にフォーカスを持っていた要素。</summary>
    public (PaneFocusTarget Target, WeakReference<TElement> Element)? Previous { get; private set; }

    public void Record(PaneFocusTarget target, TElement element)
    {
        if (_current is { } current && current.Target != target)
            Previous = current;
        _current = (target, new WeakReference<TElement>(element));
    }
}
