namespace sk0ya.Loomo.App.Layout;

/// <summary>
/// ペインの配置全体を回す・裏返す・縦横を入れ替える・中身だけを巡回させる（§24.84・Emacs 31.1 の <c>window-layout-rotate-clockwise</c>／
/// <c>window-layout-flip-leftright</c>／<c>window-layout-transpose</c>／<c>rotate-windows</c>）。横長のモニターで左右に置いていたものを
/// ノート PC で上下にしたい、を手で解体し直さずに済ませる。
/// <para>どれも<b>新しい木</b>を返す（<see cref="PaneSplit.Orientation"/> は作るときにしか決められない）。比率（<see cref="PaneNode.Weight"/>）と
/// 非表示の印はノードごと持ち越すので、並びが変わっても各ペインの大きさの割合とペインの状態はそのまま。</para>
/// </summary>
public static class PaneLayoutTransforms
{
    /// <summary>時計回りに 90° 回す。左右の並び（左→右）は上下（上→下）に、上下の並び（上→下）は左右（右→左）になる。</summary>
    public static PaneNode RotateClockwise(PaneNode node) => Map(node, split => split.Orientation == SplitKind.Columns
        ? (SplitKind.Rows, false)
        : (SplitKind.Columns, true));

    /// <summary>反時計回りに 90° 回す。左右（左→右）は上下（下→上）に、上下（上→下）は左右（左→右）になる。</summary>
    public static PaneNode RotateAnticlockwise(PaneNode node) => Map(node, split => split.Orientation == SplitKind.Columns
        ? (SplitKind.Rows, true)
        : (SplitKind.Columns, false));

    /// <summary>左右を裏返す（左右の並びだけ順を逆に）。</summary>
    public static PaneNode FlipLeftRight(PaneNode node)
        => Map(node, split => (split.Orientation, split.Orientation == SplitKind.Columns));

    /// <summary>上下を裏返す（上下の並びだけ順を逆に）。</summary>
    public static PaneNode FlipTopDown(PaneNode node)
        => Map(node, split => (split.Orientation, split.Orientation == SplitKind.Rows));

    /// <summary>縦横を入れ替える（すべての左右の分割を上下に、上下を左右に。順はそのまま）。</summary>
    public static PaneNode Transpose(PaneNode node) => Map(node, split => (
        split.Orientation == SplitKind.Columns ? SplitKind.Rows : SplitKind.Columns, false));

    /// <summary>
    /// 枠はそのまま、中身（ペイン）だけを見えている順に 1 つずつ送る（<paramref name="forward"/> なら左上の中身が次の枠へ、最後の中身が最初の枠へ）。
    /// 隠しているペインは動かさない——動かすと、再表示したときに元と違う場所へ出る。
    /// </summary>
    public static PaneNode CyclePanes(PaneNode node, bool forward = true)
    {
        var visible = PaneLayoutTree.AllLeaves(node).Where(l => !l.Hidden).ToList();
        if (visible.Count < 2)
            return node;
        var replacement = new Dictionary<PaneLeaf, PaneLeaf>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < visible.Count; i++)
        {
            var from = forward ? (i - 1 + visible.Count) % visible.Count : (i + 1) % visible.Count;
            replacement[visible[i]] = new PaneLeaf { Kind = visible[from].Kind, Weight = visible[i].Weight };
        }
        return Rebuild(node, replacement);
    }

    private static PaneNode Rebuild(PaneNode node, IReadOnlyDictionary<PaneLeaf, PaneLeaf> replacement)
    {
        if (node is PaneLeaf leaf)
            return replacement.TryGetValue(leaf, out var swapped) ? swapped : leaf;
        var split = (PaneSplit)node;
        var copy = new PaneSplit { Orientation = split.Orientation, Weight = split.Weight };
        copy.Children.AddRange(split.Children.Select(c => Rebuild(c, replacement)));
        return copy;
    }

    /// <summary>分割ごとに（新しい向き, 子の並びを逆にするか）を決めて木を作り直す。リーフはそのまま使う。</summary>
    private static PaneNode Map(PaneNode node, Func<PaneSplit, (SplitKind Orientation, bool Reverse)> decide)
    {
        if (node is not PaneSplit split)
            return node;
        var (orientation, reverse) = decide(split);
        var copy = new PaneSplit { Orientation = orientation, Weight = split.Weight };
        var children = split.Children.Select(c => Map(c, decide));
        copy.Children.AddRange(reverse ? children.Reverse() : children);
        return copy;
    }
}
