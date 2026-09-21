using System.Collections.Generic;

namespace sk0ya.Loomo.App.Services;

/// <summary>選択中の行を一覧内で1つ上下へ移す。</summary>
internal static class OrderedItemMovePolicy
{
    public static int? Move<T>(IList<T> items, int index, int offset)
    {
        var target = index + offset;
        if (index < 0 || index >= items.Count || target < 0 || target >= items.Count)
            return null;

        var item = items[index];
        items.RemoveAt(index);
        items.Insert(target, item);
        return target;
    }
}
