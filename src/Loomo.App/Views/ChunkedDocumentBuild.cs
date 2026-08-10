using System;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 差分本体の FlowDocument を<b>スライスに分けて組む</b>途中状態。「どこまで組んだか」と
/// 「範囲 <c>[start, end)</c> を文書へ追記する手続き」だけを持つ。
///
/// <para>行数ぶんの Paragraph 構築とレイアウトはどちらも UI スレッドでしかできない（FlowDocument は
/// DispatcherObject、レイアウトは当然 UI）ので、全行を一息に組むとその間ペインが固まる。少しずつ組んでは
/// Dispatcher へ戻る（＝入力と描画に順番を譲る）ために、進捗をここに預ける。</para>
///
/// <para>組み立ての途中で文書ごと差し替えられたら <see cref="Cancel"/>：予約済みのスライスは、
/// もう捨てられた文書へ書き込もうとするので走らせてはいけない。逆に、行を添字で指してくる操作
/// （ジャンプなど）の直前には <see cref="Finish"/> で追いつかせる。</para>
/// </summary>
internal sealed class ChunkedDocumentBuild
{
    private readonly Action<int, int> _append;
    private readonly int _rowCount;
    private int _next;

    internal ChunkedDocumentBuild(int rowCount, Action<int, int> append)
    {
        _rowCount = rowCount;
        _append = append;
    }

    /// <summary>まだ組み終えていない＝この文書を添字で指す操作は待つか <see cref="Finish"/> で追いつかせる。</summary>
    internal bool IsRunning => _next < _rowCount && !Cancelled;

    /// <summary>組み立ての途中で文書ごと差し替えられた。</summary>
    internal bool Cancelled { get; private set; }

    internal void Cancel() => Cancelled = true;

    /// <summary>次の1スライスを組む（<paramref name="chunk"/> 行、残りが少なければそこまで）。</summary>
    internal void Step(int chunk)
    {
        if (Cancelled || chunk <= 0) return;
        // 加算あふれを避ける（Finish は chunk に全行数を渡してくる）
        var end = chunk >= _rowCount - _next ? _rowCount : _next + chunk;
        if (end == _next) return;
        _append(_next, end);
        _next = end;
    }

    /// <summary>残り全部を一気に組む。</summary>
    internal void Finish() => Step(_rowCount);
}
