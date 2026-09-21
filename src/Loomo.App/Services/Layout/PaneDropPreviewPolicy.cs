namespace sk0ya.Loomo.App.Services;

internal readonly record struct PaneLayoutRect(double X, double Y, double Width, double Height);

internal readonly record struct PaneDropSelection(DropZone Zone, bool Center, bool NearOuterEdge);
internal enum PaneDropCommitKind { None, Stage, Wing, PlaceFromWing, MoveWithinLayout }
internal readonly record struct PaneDropCommit(
    PaneDropCommitKind Kind, PaneKind Source, PaneKind? Target, DropZone? Zone, bool Center, bool Span);

/// <summary>ポインター位置からドロップ先ゾーンとプレビュー矩形を決める。</summary>
internal static class PaneDropPreviewPolicy
{
    public static PaneDropCommit ResolveCommit(
        PaneKind source, PaneKind? target, DropZone? zone, bool center, bool span,
        bool fromWing, bool stageDrag, bool toWing)
    {
        if (stageDrag)
            return new(PaneDropCommitKind.Stage, source, target, zone, center, span);
        if (toWing)
            return new(PaneDropCommitKind.Wing, source, target, zone, center, span);
        if (target is not { } targetKind || targetKind == source)
            return new(PaneDropCommitKind.None, source, target, zone, center, span);
        if (fromWing)
            return new(PaneDropCommitKind.PlaceFromWing, source, target, zone, center, span);
        return zone is null
            ? new(PaneDropCommitKind.None, source, target, zone, center, span)
            : new(PaneDropCommitKind.MoveWithinLayout, source, target, zone, center, span);
    }

    public static PaneDropSelection Select(double relativeX, double relativeY, bool allowCenter)
    {
        var zone = NearestZone(relativeX, relativeY);
        var center = allowCenter
            && relativeX is > 0.34 and < 0.66
            && relativeY is > 0.34 and < 0.66;
        var nearOuterEdge = zone switch
        {
            DropZone.Left => relativeX < 0.2,
            DropZone.Right => relativeX > 0.8,
            DropZone.Above => relativeY < 0.2,
            _ => relativeY > 0.8,
        };
        return new(zone, center, nearOuterEdge);
    }

    public static PaneLayoutRect ZoneBounds(PaneLayoutRect bounds, DropZone zone)
        => zone switch
        {
            DropZone.Left => new(bounds.X, bounds.Y, bounds.Width / 2, bounds.Height),
            DropZone.Right => new(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
            DropZone.Above => new(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2),
            _ => new(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2),
        };

    public static bool SpanAddsBreadth(PaneLayoutRect span, PaneLayoutRect leaf, DropZone zone)
        => zone is DropZone.Above or DropZone.Below
            ? span.Width > leaf.Width + 1
            : span.Height > leaf.Height + 1;

    private static DropZone NearestZone(double relativeX, double relativeY)
    {
        var left = relativeX;
        var right = 1 - relativeX;
        var above = relativeY;
        var below = 1 - relativeY;
        var min = Math.Min(Math.Min(left, right), Math.Min(above, below));
        if (min == left) return DropZone.Left;
        if (min == right) return DropZone.Right;
        if (min == above) return DropZone.Above;
        return DropZone.Below;
    }
}

/// <summary>舞台ペインへのドロップで使う最小の2分割ツリーを構築する。</summary>
internal static class PaneStageDropPolicy
{
    public static PaneNode CreateRoot(PaneKind source, PaneKind stage, DropZone zone)
    {
        var orientation = zone is DropZone.Left or DropZone.Right
            ? SplitKind.Columns
            : SplitKind.Rows;
        var split = new PaneSplit { Orientation = orientation };
        var stageLeaf = new PaneLeaf { Kind = stage };
        var draggedLeaf = new PaneLeaf { Kind = source };
        if (zone is DropZone.Left or DropZone.Above)
        {
            split.Children.Add(draggedLeaf);
            split.Children.Add(stageLeaf);
        }
        else
        {
            split.Children.Add(stageLeaf);
            split.Children.Add(draggedLeaf);
        }
        return split;
    }
}
