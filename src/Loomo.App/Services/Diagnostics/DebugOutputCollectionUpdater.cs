using System.Collections.Specialized;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ出力コレクションの変更を、表示先へ反映する手順に変換する。</summary>
internal static class DebugOutputCollectionUpdater
{
    internal static void Rebuild(
        IEnumerable<DebugOutputLine>? lines,
        Action clear,
        Action<DebugOutputLine> append,
        Action scrollToEnd)
    {
        clear();
        if (lines is null) return;
        foreach (var line in lines) append(line);
        scrollToEnd();
    }

    internal static void ApplyChange(
        NotifyCollectionChangedEventArgs change,
        Func<bool> isAtBottom,
        Action<DebugOutputLine> append,
        Action removeFirst,
        Action clear,
        Action scrollToEnd)
    {
        switch (change.Action)
        {
            case NotifyCollectionChangedAction.Add:
                var wasAtBottom = isAtBottom();
                foreach (DebugOutputLine line in change.NewItems!) append(line);
                if (wasAtBottom) scrollToEnd();
                break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < (change.OldItems?.Count ?? 0); i++) removeFirst();
                break;
            case NotifyCollectionChangedAction.Reset:
                clear();
                break;
        }
    }

    internal static bool IsAtBottom(
        double extentHeight,
        double viewportHeight,
        double verticalOffset,
        double tolerance = 4)
        => extentHeight <= viewportHeight || verticalOffset + viewportHeight >= extentHeight - tolerance;
}
