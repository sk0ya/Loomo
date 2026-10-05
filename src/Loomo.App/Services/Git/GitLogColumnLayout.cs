namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Git コミット一覧の列の並びと表示／非表示の計算（WPF に触れない純粋関数）。
///
/// <para>並びは<b>隠している列も含めて</b>持つ。表示に戻した列が末尾ではなく元の位置へ戻るように
/// するため。見出しのドラッグで変わるのは「見えている列どうしの順」だけなので、それを全体の並びへ
/// 戻すときは、見えている列が占めていた枠だけを新しい順で埋め直す（<see cref="MergeVisibleOrder"/>）。</para>
///
/// <para>「コミット」列は隠せない。グラフ・参照・件名を持つ一覧の本体で、余りの幅を吸う列でもある
/// （隠すと何の一覧か分からなくなる）。並べ替えはできる。</para>
/// </summary>
internal static class GitLogColumnLayout
{
    internal const string Commit = "Commit";
    internal const string Date = "Date";
    internal const string Author = "Author";
    internal const string Id = "Id";

    internal static readonly IReadOnlyList<string> DefaultOrder = [Commit, Date, Author, Id];

    internal static bool CanHide(string id) => id != Commit;

    /// <summary>保存値を、既知の列を1回ずつ含む並びへ整える（未知の ID と重複は捨て、足りない列は既定の位置へ差し込む）。</summary>
    internal static IReadOnlyList<string> NormalizeOrder(IEnumerable<string>? saved)
    {
        var order = new List<string>();
        foreach (var id in saved ?? [])
            if (DefaultOrder.Contains(id) && !order.Contains(id))
                order.Add(id);
        // 足りない列（設定に無い＝後から増えた列など）は、既定の並びで直前に来る列の後ろへ置く。
        for (var i = 0; i < DefaultOrder.Count; i++)
        {
            var id = DefaultOrder[i];
            if (order.Contains(id)) continue;
            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var prev = order.IndexOf(DefaultOrder[j]);
                if (prev >= 0) { at = prev + 1; break; }
            }
            order.Insert(at, id);
        }
        return order;
    }

    /// <summary>隠している列（既知で、隠せるものだけ）。</summary>
    internal static IReadOnlyList<string> NormalizeHidden(IEnumerable<string>? saved)
        => (saved ?? []).Where(id => DefaultOrder.Contains(id) && CanHide(id)).Distinct().ToList();

    /// <summary>実際に一覧へ並べる列（並び順のうち、隠していないもの）。</summary>
    internal static IReadOnlyList<string> Visible(IReadOnlyList<string> order, IReadOnlyList<string> hidden)
        => order.Where(id => !hidden.Contains(id)).ToList();

    /// <summary>見えている列の新しい順を全体の並びへ戻す。隠している列は今の位置に残す。</summary>
    internal static IReadOnlyList<string> MergeVisibleOrder(
        IReadOnlyList<string> order, IReadOnlyList<string> hidden, IReadOnlyList<string> visible)
    {
        var queue = new Queue<string>(visible.Where(id => order.Contains(id) && !hidden.Contains(id)));
        var merged = new List<string>(order.Count);
        foreach (var id in order)
        {
            if (hidden.Contains(id) || !visible.Contains(id)) merged.Add(id);
            else merged.Add(queue.Dequeue());
        }
        return merged;
    }

    /// <summary>見えている列の中で、<paramref name="id"/> を左（-1）／右（+1）の隣と入れ替える。端なら変えない。</summary>
    internal static IReadOnlyList<string> MoveVisible(
        IReadOnlyList<string> order, IReadOnlyList<string> hidden, string id, int direction)
    {
        var visible = Visible(order, hidden).ToList();
        var index = visible.IndexOf(id);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= visible.Count) return order;
        (visible[index], visible[target]) = (visible[target], visible[index]);
        return MergeVisibleOrder(order, hidden, visible);
    }

    /// <summary>列見出しの表示名。</summary>
    internal static string Title(string id) => id switch
    {
        Commit => "コミット",
        Date => "日時",
        Author => "作成者",
        Id => "ID",
        _ => id,
    };
}
