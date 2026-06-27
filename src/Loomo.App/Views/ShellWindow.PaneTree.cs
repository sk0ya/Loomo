using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

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

        CaptureLayoutSizes();
        _enabledSessions.Add(dragged);   // タイルに出る＝有効

        _root = PlaceInTree(_root, dragged, target, center, zone, span);
        // 跨ぎ最大化中は、解除時に戻す保存レイアウトへも同じ配置を反映する。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = PlaceInTree(savedRoot, dragged, target, center, zone, span);

        MarkLayoutDirty();
        RebuildPaneLayout();
        FocusPane(dragged);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ツリーへ <paramref name="dragged"/> を配置した新しいルートを返す。入れ替えなら
    /// ターゲットの位置へ据えてターゲットをツリーから外し（＝袖へ）、挿入ならターゲットの指定辺へ分割する。</summary>
    private static PaneNode? PlaceInTree(PaneNode? root, PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false)
    {
        if (root is null)
            return root;
        var targetLeaf = PaneLayoutTree.FindLeaf(root, target);
        if (targetLeaf is null)
            return root;

        // 既にツリーに在る（隠れているなど）なら取り外して、配置先を一意にする。
        if (PaneLayoutTree.FindLeaf(root, dragged) is { } existing)
            root = RemoveNode(root, existing);

        if (center)
        {
            // 入れ替え：ターゲットの位置と比率をそのまま引き継ぎ、ターゲットを外す＝ターゲットの場所を引き継ぐ。
            var targetWeight = targetLeaf.Weight > 0 ? targetLeaf.Weight : 1;
            var leaf = new PaneLeaf { Kind = dragged, Weight = targetWeight };
            root = InsertRelative(root, leaf, targetLeaf, DropZone.Left);
            root = RemoveNode(root, targetLeaf);
            // InsertRelative は分割挿入用に target/node の取り分を半分ずつへ割る。入れ替えでは
            // ターゲットの取り分を丸ごと引き継ぐので、半割りされた重みを元の比率へ戻す
            // （これをしないと入れ替えのたびに位置の幅が半分へ寄り、繰り返すとレイアウトがズレる）。
            leaf.Weight = targetWeight;
        }
        else
        {
            var z = zone ?? DropZone.Right;
            // スパン挿入なら単体ペインでなく、当該辺を端まで占める祖先スプリットへ落とす。
            PaneNode insertTarget = span ? PaneLayoutTree.ResolveSpanTarget(root, targetLeaf, z) : targetLeaf;
            root = InsertRelative(root, new PaneLeaf { Kind = dragged }, insertTarget, z);
        }
        return Normalize(root);
    }

    /// <summary>ノードを親スプリットから取り外し、新しいルートを返す（畳み込みは Normalize に任せる）。</summary>
    private static PaneNode? RemoveNode(PaneNode? root, PaneNode node) => PaneLayoutTree.RemoveNode(root, node);

    /// <summary>
    /// <paramref name="node"/> を <paramref name="target"/> の指定した辺へ挿入し、新しいルートを返す
    /// （実体は <see cref="PaneLayoutTree.InsertRelative"/>）。
    /// </summary>
    private static PaneNode? InsertRelative(PaneNode? root, PaneNode node, PaneNode target, DropZone zone)
        => PaneLayoutTree.InsertRelative(root, node, target, zone);
}

