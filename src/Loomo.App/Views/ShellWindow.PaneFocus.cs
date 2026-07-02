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

/// <summary>ShellWindow: フォーカス追跡と方向移動（Ctrl+W h/j/k/l）。フォーカス領域の記録、隣接領域の探索、
/// ビューポート/サイドバー/ペインへのフォーカス適用、ペイン/サイドバー矩形の取得。
/// キー入口・リサイズモードは ShellWindow.PaneNavigation.cs。</summary>
public partial class ShellWindow
{
    /// <summary>キーボードフォーカスが入ったペインを記録する（移動の起点に使う）。</summary>
    private void OnWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // フォーカスが他所へ移ったら、待ち状態のプレフィックス連鎖は破棄する
        // （Ctrl+W → 気が変わってクリック/別ペインへ移動 → 後続の h/j/k/l が誤って奪われるのを防ぐ）。
        // リサイズ自身が起こすフォーカス移動（ガード中）以外でフォーカスが動いたら、
        // ユーザー操作とみなしてリサイズモードも終了する。
        _keyboard?.OnExternalFocusChange(suppressModeExit: _suppressResizeExit);

        if (e.NewFocus is not DependencyObject d)
            return;
        if (FindPaneOf(d) is { } kind)
        {
            // 分割中ならどのビューポートが取得したかまで記録する（hjkl 移動の起点に使う）。
            if (ViewsFor(kind) is { } views && views.SetFocusedFromElement(d) is { } viewId)
                _focusedRegion = FocusTarget.Viewport(kind, viewId);
            else
                _focusedRegion = FocusTarget.Of(kind);
            // 別ペインへのフォーカス移動を軌跡（操作ログ）へ記録する（同一ペイン内は増やさない）。
            RecordTrailPane(kind);
        }
        else if (IsWithin(d, SidebarContainer))
            _focusedRegion = FocusTarget.Sidebar;
    }

    /// <summary>要素が指定の祖先（論理・視覚いずれか）の内側にあるか。</summary>
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = GetAnyParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    /// <summary>ウィンドウが非アクティブになったらプレフィックス待ち・リサイズモードを解除する。</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
        => _keyboard?.Reset();

    /// <summary>要素を内包するペイン種別を視覚ツリーを遡って特定する（ペイン外なら null）。</summary>
    private PaneKind? FindPaneOf(DependencyObject element)
    {
        for (var current = element; current is not null; current = GetAnyParent(current))
        {
            foreach (var (kind, paneElement) in _paneElements)
                if (ReferenceEquals(paneElement, current))
                    return kind;
        }
        return null;
    }

    private static DependencyObject? GetAnyParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    /// <summary>
    /// 起点領域から指定方向で最も近い隣接領域へフォーカスを移す。候補にはペイン本体に加え、
    /// 表示中ならサイドバー（Explorer 等）も含めるので、最左ペインから Ctrl+W h でサイドバーへ移れる。
    /// </summary>
    private void FocusPaneInDirection(DropZone direction)
    {
        // ソロモード中でも、Editor/Terminal の内部分割がある場合は vim 風に
        // そのペイン内のビューポート移動を優先する。端まで来たら舞台を切り替える。
        if (_stageActive && _focusedRegion?.Pane is { } stageFocused
            && ViewsFor(stageFocused) is { LeafCount: > 1 } stageViews)
        {
            if (stageViews.FocusInDirection(direction, PaneHost)
                && stageViews.FocusedViewportId is { } viewportId)
            {
                _focusedRegion = FocusTarget.Viewport(stageFocused, viewportId);
                SyncActiveFromViewport(stageFocused);
                return;
            }
        }

        // ソロモード中の h/j/k/l は「舞台の転換」（並び順で前後のペインへ）と読み替える。
        if (_stageActive)
        {
            CycleStage(StageCycleDirection(direction));
            return;
        }
        var targets = FocusTargets().ToList();
        if (targets.Count == 0)
            return;

        // 起点：直近フォーカスの領域。見つからなければ最初の候補（=可視ペイン）を起点扱いにする。
        var originIndex = _focusedRegion is { } region
            ? targets.FindIndex(t => t.Target == region)
            : -1;
        if (originIndex < 0)
            originIndex = 0;
        var (originTarget, from) = targets[originIndex];

        var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        FocusTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var (target, r) in targets)
        {
            if (target == originTarget)
                continue;

            // 指定方向の側にある領域だけを候補にする（タイル配置なので辺で判定）。
            const double tolerance = 1.0;
            var inDirection = direction switch
            {
                DropZone.Left => r.X + r.Width <= from.X + tolerance,
                DropZone.Right => r.X >= from.X + from.Width - tolerance,
                DropZone.Above => r.Y + r.Height <= from.Y + tolerance,
                _ => r.Y >= from.Y + from.Height - tolerance,
            };
            if (!inDirection)
                continue;

            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            // 移動軸方向の距離を主に、直交方向のずれを従にして最も近い領域を選ぶ。
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
            var score = axis + perpendicular * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = target;
            }
        }

        if (best is { } target2)
            ApplyFocusTarget(target2);
    }

    private static int StageCycleDirection(DropZone direction)
        => direction is DropZone.Below or DropZone.Right ? 1 : -1;

    /// <summary>ナビゲーション候補（表示中ペイン＋サイドバー）を矩形付きで列挙する。ペインを先頭に並べる。</summary>
    private IEnumerable<(FocusTarget Target, Rect Rect)> FocusTargets()
    {
        foreach (var leaf in AllLeaves())
        {
            if (leaf.Hidden)
                continue;
            // 内部分割しているペインはビューポート単位、それ以外はペイン全体を1候補にする。
            if (ViewsFor(leaf.Kind) is { LeafCount: > 1 } views)
            {
                foreach (var (id, rect) in views.ViewportRects(PaneHost))
                    yield return (FocusTarget.Viewport(leaf.Kind, id), rect);
            }
            else if (TryGetPaneRect(leaf.Kind, out var rect))
            {
                yield return (FocusTarget.Of(leaf.Kind), rect);
            }
        }

        if (TryGetSidebarRect(out var sidebarRect))
            yield return (FocusTarget.Sidebar, sidebarRect);
    }

    /// <summary>そのペインの内部分割マネージャ（Editor/Terminal のみ。それ以外は null）。</summary>
    private PaneSplitView? ViewsFor(PaneKind kind) => kind switch
    {
        PaneKind.Editor => _editorViews,
        PaneKind.Terminal => _terminalViews,
        _ => null
    };

    /// <summary>サイドバーの矩形（PaneHost 座標系）を取得する。非表示・未配置なら false。</summary>
    private bool TryGetSidebarRect(out Rect rect)
    {
        rect = default;
        if (!_vm.IsSidebarVisible || !SidebarContainer.IsVisible
            || SidebarContainer.ActualWidth <= 0 || SidebarContainer.ActualHeight <= 0)
            return false;

        // サイドバーは PaneHost の左隣にあるため X は負になるが、辺判定・距離計算はそのまま成立する。
        var topLeft = SidebarContainer.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(SidebarContainer.ActualWidth, SidebarContainer.ActualHeight));
        return true;
    }

    private void ApplyFocusTarget(FocusTarget target)
    {
        if (target.IsSidebar)
        {
            FocusSidebar();
            return;
        }

        var kind = target.Pane!.Value;
        if (target.ViewportId != default && ViewsFor(kind) is { } views)
        {
            views.FocusViewport(target.ViewportId);
            _focusedRegion = target;
            SyncActiveFromViewport(kind);
        }
        else
        {
            FocusPane(kind);
        }
    }

    /// <summary>フォーカス中ビューポートのタブに合わせて strip 強調・サービスアタッチを追従させる。</summary>
    private void SyncActiveFromViewport(PaneKind kind)
    {
        if (kind == PaneKind.Editor && _editorViews?.FocusedTabId is { } eid
            && _editorTabs.FirstOrDefault(t => t.Id == eid) is { } et)
            SetActiveEditorTab(et);
        else if (kind == PaneKind.Terminal && _terminalViews?.FocusedTabId is { } tid
            && _terminalTabs.FirstOrDefault(t => t.Id == tid) is { } tt)
            SetActiveTerminalTab(tt);
    }

    /// <summary>表示中のサイドバー（Explorer 等）へキーボードフォーカスを移す。</summary>
    private void FocusSidebar()
    {
        if (!_vm.IsSidebarVisible)
            return;

        var view = SidebarContainer.Children.OfType<UIElement>()
            .FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (view is null)
            return;

        _focusedRegion = FocusTarget.Sidebar;
        if (view is FolderTreeView tree)
            tree.FocusTree();           // Explorer は中身のツリーへ直接フォーカス（先頭未選択なら選ぶ）
        else
            FocusFirstFocusable(view);  // 他パネルは最初のフォーカス可能要素へ
    }

    /// <summary>要素ツリーを深さ優先でたどり、最初のフォーカス可能要素へフォーカスを移す。</summary>
    private static bool FocusFirstFocusable(DependencyObject root)
    {
        if (root is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element)
        {
            element.Focus();
            return true;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FocusFirstFocusable(VisualTreeHelper.GetChild(root, i)))
                return true;
        return false;
    }

    /// <summary>ペイン本体の矩形（PaneHost 座標系）を取得する。非表示・未配置なら false。</summary>
    private bool TryGetPaneRect(PaneKind kind, out Rect rect)
    {
        rect = default;
        if (!_paneElements.TryGetValue(kind, out var element)
            || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var topLeft = element.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return true;
    }

    /// <summary>指定ペインのアクティブな中身へキーボードフォーカスを移す。</summary>
    private void FocusPane(PaneKind kind)
    {
        // ソロモード中は、フォーカス対象を舞台へ立てる（AI がファイルを開いた・
        // 差分を出した等の既存フローがそのまま「舞台の自動転換」になる）。
        if (_stageActive && kind != _stagePane)
            SetStagePane(kind);
        _focusedRegion = FocusTarget.Of(kind);
        switch (kind)
        {
            case PaneKind.Terminal:
                if (_terminalViews is { } tv) tv.FocusFocused();
                else _activeTerminalTab?.View.FocusTerminal();
                break;
            case PaneKind.Editor:
                if (_editorViews is { } ev) ev.FocusFocused();
                else _activeEditorTab?.Control.Focus();
                break;
            case PaneKind.EditorSupport:
                _editorSupportView?.Focus();
                break;
            case PaneKind.Browser:
                _activeBrowserTab?.View.Focus();
                break;
            case PaneKind.Ai:
                AiBarHost.FocusInput();
                break;
            case PaneKind.Git:
                GitSessionHost.Focus();
                break;
            case PaneKind.Diff:
                DiffSessionHost.Focus();
                break;
            case PaneKind.Trace:
                TraceSessionHost.Focus();
                break;
        }
    }
}

