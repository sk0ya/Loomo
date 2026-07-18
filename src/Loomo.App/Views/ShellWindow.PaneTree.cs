
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: レイアウトツリーの変更操作（ペイン移動・袖からの配置・入れ替え・挿入・除去）。
/// ツリーの構築/描画は ShellWindow.PaneLayout.cs、ドラッグ操作は ShellWindow.PaneDrag.cs。</summary>
public partial class ShellWindow
{
    /// <summary>ペインを移動する。<paramref name="span"/> なら単体ペインでなく、ターゲットの当該辺を
    /// 端まで占めるスプリット全体の辺へ落とす（例：左右2ペインの下へフル幅で挿入）。</summary>
    private void MovePane(PaneKind source, PaneKind target, DropZone zone, bool span = false)
    {
        if (source == target)
            return;

        var sourceLeaf = FindLeaf(source);
        var targetLeaf = FindLeaf(target);
        if (sourceLeaf is null || targetLeaf is null)
            return;

        BeginTrailLayoutChange();

        CaptureLayoutSizes();

        // 移動元をツリーから外し、ターゲット（またはスパン対象の祖先）の指定した辺へ挿入する。
        _root = RemoveNode(_root, sourceLeaf);
        sourceLeaf.Weight = 1;
        var insertTarget = span ? PaneLayoutTree.ResolveSpanTarget(_root, targetLeaf, zone) : targetLeaf;
        _root = InsertRelative(_root, sourceLeaf, insertTarget, zone);

        // 跨ぎ最大化中の移動は、解除時に戻す保存レイアウトへも同じ論理操作を反映する
        // （解除やスナップショット保存で移動が巻き戻らないように）。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = MoveInTree(savedRoot, source, target, zone, span);

        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>指定ツリー上で <see cref="MovePane"/> と同じ移動を行い、新しいルートを返す。</summary>
    private static PaneNode? MoveInTree(PaneNode root, PaneKind source, PaneKind target, DropZone zone, bool span = false)
        => PaneLayoutTree.MoveInTree(root, source, target, zone, span);

    /// <summary>袖（ミニチュア）のペインをタイルへ配置する。<paramref name="center"/> なら入れ替え
    /// （ターゲットの位置へ据え、元のペインは袖へ退場）、それ以外は <paramref name="zone"/> の辺へ分割挿入する。</summary>
    private void PlaceWingPane(PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false)
    {
        if (dragged == target || FindLeaf(target) is null)
            return;

        BeginTrailLayoutChange();

        CaptureLayoutSizes();
        _enabledSessions.Add(dragged);   // タイルに出る＝有効

        _paneLayout.Place(dragged, target, center, zone, span);
        // 跨ぎ最大化中は、解除時に戻す保存レイアウトへも同じ配置を反映する。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = PaneLayoutCoordinator.Place(savedRoot, dragged, target, center, zone, span);

        MarkLayoutDirty();
        RebuildPaneLayout();
        FocusPane(dragged);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ツリーへ <paramref name="dragged"/> を配置した新しいルートを返す。入れ替えなら
    /// ターゲットの位置へ据えてターゲットをツリーから外し（＝袖へ）、挿入ならターゲットの指定辺へ分割する。</summary>
    /// <summary>ノードを親スプリットから取り外し、新しいルートを返す（畳み込みは Normalize に任せる）。</summary>
    private static PaneNode? RemoveNode(PaneNode? root, PaneNode node) => PaneLayoutTree.RemoveNode(root, node);

    /// <summary>
    /// <paramref name="node"/> を <paramref name="target"/> の指定した辺へ挿入し、新しいルートを返す
    /// （実体は <see cref="PaneLayoutTree.InsertRelative"/>）。
    /// </summary>
    private static PaneNode? InsertRelative(PaneNode? root, PaneNode node, PaneNode target, DropZone zone)
        => PaneLayoutTree.InsertRelative(root, node, target, zone);
}

